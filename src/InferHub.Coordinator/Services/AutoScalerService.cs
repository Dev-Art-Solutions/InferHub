using System.Collections.Concurrent;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 76: reacts to <see cref="Metrics.RecordCapabilityUnavailable"/> by re-enabling a model on a
/// node that already holds it but has it disabled (phase 74), instead of leaving that discovery to an
/// operator staring at logs. It calls <see cref="NodeModelToggle"/> — the same profile read-modify-write
/// and the same VRAM precheck a human hits through the console — so nothing here can grant a model
/// room a human admin would have been refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>The signal is <c>Metrics</c>, not <see cref="IUsageLedger"/>.</b> The first cut of this phase
/// read <c>IUsageLedger.FallbackRequests</c>, on the assumption that a disabled model would make
/// requests fall back the same way an unheld one does. It does not: <c>FleetSaturation</c> (which
/// gates cloud burst) and <c>RequestQueue</c> both call <c>INodeRegistry.FindNodesWithModel</c> with
/// no capability filter, which matches raw inventory — a disabled model is still "held", so fallback
/// never fires for it, and <c>FallbackRequests</c> would have sat at zero for the one case this phase
/// exists to fix. The real 503 a caller gets for a disabled model ("no node currently provides
/// '{capability}' for model '{model}'", <c>InferenceCore.DispatchAsync</c>) had no counter before this
/// phase; <see cref="Metrics.RecordCapabilityUnavailable"/> is that counter, added alongside this
/// service rather than reusing a metric that measures a different failure.
/// </para>
/// <para>
/// <b>Scale-out only.</b> There is still no per-node dimension anywhere that would justify a
/// scale-*in* decision — <see cref="Metrics.CapabilityUnavailableByModel"/>, like the usage ledger,
/// knows the model, never which node's copy sat idle. Inventing one from silence would be the kind of
/// guess <c>NodeModelToggle.Precheck</c> already refuses to make about a model's resident size. A
/// scale-in phase can follow once a node-attributed signal exists; it is a non-goal here.
/// </para>
/// <para>
/// <b>No pulls.</b> A candidate is a node that already has the model in <see cref="INodeRegistry.ModelInventory"/> —
/// on disk — but does not currently declare it as a routable capability. Downloading a model onto a
/// node that never had it is a multi-minute job with its own existing manual flow
/// (<c>ModelCommandCoordinator</c>, phase 26/48) and its own bandwidth/disk cost; auto-triggering that
/// is a separate, riskier feature.
/// </para>
/// <para>
/// <b>Never forces.</b> <c>force=true</c> is how a human overrides the VRAM precheck's refusal; the
/// scaler never passes it, so it can only ever do what the precheck already agrees fits.
/// </para>
/// </remarks>
public sealed class AutoScalerService(
    Metrics metrics,
    INodeRegistry registry,
    NodeModelToggle toggle,
    IConfiguration configuration,
    ILogger<AutoScalerService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(15);
    private const long DefaultPressureThreshold = 5;

    /// <summary>
    /// Per model, the cumulative <see cref="Metrics.CapabilityUnavailableByModel"/> count as of the
    /// last tick. The counter is cumulative for the process lifetime; a tick acts on the <em>delta</em>
    /// since the previous tick, which is what "pressured right now" actually means.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> lastObservedCount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per (nodeId, model), when the scaler last acted on it — the flap guard.</summary>
    private readonly ConcurrentDictionary<(string NodeId, string Model), DateTimeOffset> lastActionUtc = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("AutoScaling:Enabled", false))
        {
            logger.LogInformation("Auto-scaler disabled (AutoScaling:Enabled is not true)");
            return;
        }

        var interval = GetSeconds("AutoScaling:IntervalSeconds", DefaultInterval);
        var dryRun = configuration.GetValue("AutoScaling:DryRun", true);

        logger.LogInformation(
            "Auto-scaler starting: interval {IntervalSeconds}s, dry-run {DryRun}",
            interval.TotalSeconds, dryRun);

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await TickAsync(dryRun, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A bad tick must not take the loop down — the next tick tries again.
                logger.LogError(ex, "Auto-scaler tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var threshold = configuration.GetValue("AutoScaling:PressureThreshold", DefaultPressureThreshold);
        var cooldown = GetSeconds("AutoScaling:CooldownMinutes", DefaultCooldown, minutes: true);

        var now = DateTimeOffset.UtcNow;

        var pressured = metrics.CapabilityUnavailableByModel()
            .Select(pair => new { Model = pair.Key, Delta = pair.Value - lastObservedCount.GetOrAdd(pair.Key, 0) })
            .Where(row => row.Delta >= threshold)
            .OrderByDescending(row => row.Delta)
            .ToArray();

        // Every model's watermark advances regardless of whether it crossed the threshold, so a
        // model that stays just under it forever does not silently accumulate an ever-growing delta.
        foreach (var pair in metrics.CapabilityUnavailableByModel())
        {
            lastObservedCount[pair.Key] = pair.Value;
        }

        if (pressured.Length == 0)
        {
            return;
        }

        var inventory = registry.ModelInventory();
        var snapshots = registry.Snapshot(now)
            .ToDictionary(n => n.NodeId, StringComparer.OrdinalIgnoreCase);

        foreach (var row in pressured)
        {
            // One node per model per tick: if pressure is still elevated next tick, that is this
            // tick's evidence the first move was not enough, not a reason to have moved faster — the
            // same hysteresis NodeReaper's fixed interval gives eviction.
            var candidate = FindScaleOutCandidate(row.Model, inventory, snapshots, cooldown, now);

            if (candidate is null)
            {
                continue;
            }

            var (node, precheck) = candidate.Value;

            if (dryRun)
            {
                logger.LogInformation(
                    "Auto-scaler (dry-run): would enable '{Model}' on node {NodeId} ({Delta} capability-unavailable refusals since the last tick; {Reason})",
                    row.Model, node.NodeId, row.Delta, precheck.Reason);
                continue;
            }

            var outcome = await toggle.SetEnabledAsync(node, row.Model, enabled: true, force: false, by: "auto-scaler", cancellationToken);

            if (outcome.Applied)
            {
                lastActionUtc[(node.NodeId, row.Model)] = now;
                logger.LogWarning(
                    "Auto-scaler enabled '{Model}' on node {NodeId} ({Delta} capability-unavailable refusals since the last tick)",
                    row.Model, node.NodeId, row.Delta);
            }
            else
            {
                // The precheck can flip between the scan above and the write below if another
                // change lands concurrently; losing the race is not an error, just this tick's
                // no-op — the next tick re-evaluates from scratch.
                logger.LogInformation(
                    "Auto-scaler skipped enabling '{Model}' on node {NodeId}: {Reason}",
                    row.Model, node.NodeId, outcome.ConflictError ?? outcome.Precheck?.Reason ?? "precheck declined");
            }
        }
    }

    private (NodeSnapshot Node, ModelPrecheckResult Precheck)? FindScaleOutCandidate(
        string model,
        IReadOnlyCollection<NodeModelInventory> inventory,
        IReadOnlyDictionary<string, NodeSnapshot> snapshots,
        TimeSpan cooldown,
        DateTimeOffset now)
    {
        foreach (var entry in inventory)
        {
            if (entry.Cordoned || !entry.SupportsModelManagement)
            {
                continue;
            }

            if (!entry.Models.Any(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // does not hold the model at all — a pull, not a toggle
            }

            if (!snapshots.TryGetValue(entry.NodeId, out var node))
            {
                continue;
            }

            if (node.BackendHealth is { } health && health != BackendHealth.Healthy)
            {
                continue;
            }

            var alreadyRoutable = node.Capabilities?
                .Any(c => c.Models.Contains(model, StringComparer.OrdinalIgnoreCase)) ?? false;

            if (alreadyRoutable)
            {
                continue; // already serving it — not a scale-out target
            }

            if (lastActionUtc.TryGetValue((node.NodeId, model), out var lastAction) && now - lastAction < cooldown)
            {
                continue;
            }

            var precheck = toggle.Precheck(node, model);

            if (!precheck.Ok)
            {
                continue; // would need force=true, which the auto-scaler never passes
            }

            return (node, precheck);
        }

        return null;
    }

    private TimeSpan GetSeconds(string key, TimeSpan fallback, bool minutes = false)
    {
        var value = configuration.GetValue<double?>(key);

        if (value is not > 0)
        {
            return fallback;
        }

        return minutes ? TimeSpan.FromMinutes(value.Value) : TimeSpan.FromSeconds(value.Value);
    }
}
