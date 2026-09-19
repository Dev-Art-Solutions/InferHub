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
/// <b>Scale-out and scale-in.</b> <c>TickAsync</c> is scale-out, unchanged since phase 76.
/// <c>TickScaleInAsync</c> (phase 78) is the mirror: it disables an enabled, routable
/// <c>(node, model)</c> pair nobody has actually been routed to in a while, reading the
/// node-attributed signal (<see cref="Metrics.RecordModelServed"/>) phase 76 named as missing when it
/// declined to build this. Gated by its own <c>AutoScaling:ScaleIn:Enabled</c>, independent of
/// scale-out's <c>AutoScaling:Enabled</c>, so an upgraded fleet already running scale-out sees no
/// behaviour change until an operator opts in.
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

    /// <summary>Phase 78 scale-in defaults.</summary>
    private static readonly TimeSpan DefaultIdleMinutes = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DefaultMinUptime = TimeSpan.FromMinutes(30);

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

            try
            {
                if (configuration.GetValue("AutoScaling:ScaleIn:Enabled", false))
                {
                    await TickScaleInAsync(dryRun, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Auto-scaler scale-in tick failed");
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

    /// <summary>
    /// Phase 78: the mirror of <see cref="TickAsync"/> — disables an enabled, routable
    /// <c>(node, model)</c> pair nobody has actually been routed to in a while, as long as at least
    /// one other node still serves that model. Never runs unless <c>AutoScaling:ScaleIn:Enabled</c> is
    /// set, independently of scale-out's own <c>AutoScaling:Enabled</c>.
    /// </summary>
    private async Task TickScaleInAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var minUptime = GetSeconds("AutoScaling:ScaleIn:MinUptimeMinutes", DefaultMinUptime, minutes: true);

        if (DateTimeOffset.UtcNow - metrics.StartedAtUtc < minUptime)
        {
            return; // D3: too soon after boot to read silence as idleness.
        }

        var idleAfter = GetSeconds("AutoScaling:ScaleIn:IdleMinutes", DefaultIdleMinutes, minutes: true);
        var cooldown = GetSeconds("AutoScaling:CooldownMinutes", DefaultCooldown, minutes: true);
        var now = DateTimeOffset.UtcNow;

        var snapshots = registry.Snapshot(now).ToArray();

        // D2: how many healthy, uncordoned nodes currently route each model — a pair is never a
        // candidate if it is the only one left, no matter how idle.
        var routableNodeCountByModel = RoutableNodeCountByModel(snapshots);

        foreach (var node in snapshots)
        {
            if (node.Cordoned || !node.SupportsModelManagement)
            {
                continue;
            }

            if (node.BackendHealth is { } health && health != BackendHealth.Healthy)
            {
                continue;
            }

            foreach (var capability in node.Capabilities ?? [])
            {
                foreach (var model in capability.Models)
                {
                    routableNodeCountByModel.TryGetValue(model, out var routableCount);
                    DateTimeOffset? lastAction = lastActionUtc.TryGetValue((node.NodeId, model), out var action) ? action : null;
                    var lastServed = metrics.LastServedUtc(node.NodeId, model);

                    if (!ShouldScaleIn(routableCount, lastAction, lastServed, metrics.StartedAtUtc, now, idleAfter, cooldown))
                    {
                        continue;
                    }

                    var idleSince = lastServed ?? metrics.StartedAtUtc;

                    if (dryRun)
                    {
                        logger.LogInformation(
                            "Auto-scaler (dry-run): would disable '{Model}' on node {NodeId} (idle {IdleMinutes:F0}m, {RoutableCount} other node(s) still serve it)",
                            model, node.NodeId, (now - idleSince).TotalMinutes, routableCount - 1);
                        continue;
                    }

                    var outcome = await toggle.SetEnabledAsync(node, model, enabled: false, force: false, by: "auto-scaler", cancellationToken);

                    if (outcome.Applied)
                    {
                        lastActionUtc[(node.NodeId, model)] = now;
                        logger.LogWarning(
                            "Auto-scaler disabled '{Model}' on node {NodeId} (idle {IdleMinutes:F0}m, {RoutableCount} other node(s) still serve it)",
                            model, node.NodeId, (now - idleSince).TotalMinutes, routableCount - 1);
                    }
                    else
                    {
                        logger.LogInformation(
                            "Auto-scaler skipped disabling '{Model}' on node {NodeId}: {Reason}",
                            model, node.NodeId, outcome.ConflictError ?? "profile write declined");
                    }
                }
            }
        }
    }

    /// <summary>
    /// D2: for each model, how many currently healthy, uncordoned nodes declare it as a routable
    /// capability. Pulled out of <see cref="TickScaleInAsync"/> so it is testable without a live
    /// <see cref="INodeRegistry"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> RoutableNodeCountByModel(IReadOnlyCollection<NodeSnapshot> snapshots) =>
        snapshots
            .Where(n => !n.Cordoned && (n.BackendHealth is null || n.BackendHealth == BackendHealth.Healthy))
            .SelectMany(n => (n.Capabilities ?? []).SelectMany(c => c.Models).Select(m => (Model: m, n.NodeId)))
            .GroupBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.NodeId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The per-pair decision (D2, D3, D4), as a pure function of already-looked-up state — testable
    /// without constructing the service's DI graph. <paramref name="lastServed"/> null means "never
    /// observed", which counts as idle since <paramref name="processStartUtc"/> (D3).
    /// </summary>
    internal static bool ShouldScaleIn(
        int routableNodeCount,
        DateTimeOffset? lastAction,
        DateTimeOffset? lastServed,
        DateTimeOffset processStartUtc,
        DateTimeOffset now,
        TimeSpan idleAfter,
        TimeSpan cooldown)
    {
        if (routableNodeCount <= 1)
        {
            return false; // D2: never the last (or only) routable copy fleet-wide.
        }

        if (lastAction is { } action && now - action < cooldown)
        {
            return false; // D4: shared cooldown with scale-out, checked both directions.
        }

        var idleSince = lastServed ?? processStartUtc;
        return now - idleSince >= idleAfter;
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
