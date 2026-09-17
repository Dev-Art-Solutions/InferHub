using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 76: reacts to fallback pressure recorded in the usage ledger (phase 25) by re-enabling a
/// model on a node that already holds it but has it disabled (phase 74), instead of leaving that
/// discovery to an operator staring at <c>/api/admin/usage</c>. It calls <see cref="NodeModelToggle"/> —
/// the same profile read-modify-write and the same VRAM precheck a human hits through the console —
/// so nothing here can grant a model room a human admin would have been refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scale-out only.</b> There is no per-node dimension anywhere in <see cref="IUsageLedger"/> — a
/// <see cref="UsageRecord"/> knows the client and the model, never which node served it — so there is
/// no real signal to decide "this node's copy of the model is idle, disable it." Inventing one from
/// silence would be the kind of guess this codebase refuses to make (the VRAM precheck already labels
/// its own estimate rather than pretending it measured something). A scale-in phase can follow once a
/// node-attributed usage signal exists; it is a non-goal here, not an oversight.
/// </para>
/// <para>
/// <b>No pulls.</b> A candidate is a node that already has the model in <see cref="INodeRegistry.ModelInventory"/> —
/// on disk — but does not currently declare it as a routable capability (phase 74's disabled list, or
/// an unhealthy/cordoned node). Downloading a model onto a node that never had it is a multi-minute
/// job with its own existing manual flow (<c>ModelCommandCoordinator</c>, phase 26/48) and its own
/// bandwidth/disk cost; auto-triggering that is a separate, riskier feature.
/// </para>
/// <para>
/// <b>Never forces.</b> <c>force=true</c> is how a human overrides the VRAM precheck's refusal; the
/// scaler never passes it, so it can only ever do what the precheck already agrees fits.
/// </para>
/// </remarks>
public sealed class AutoScalerService(
    IUsageLedger usageLedger,
    INodeRegistry registry,
    NodeModelToggle toggle,
    IConfiguration configuration,
    ILogger<AutoScalerService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultLookback = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(15);
    private const long DefaultFallbackThreshold = 5;

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
                // A bad tick must not take the loop down — the next tick tries again, the way
                // usage recording never fails the request it meters (IUsageLedger.RecordAsync).
                logger.LogError(ex, "Auto-scaler tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var lookback = GetSeconds("AutoScaling:LookbackMinutes", DefaultLookback, minutes: true);
        var threshold = configuration.GetValue("AutoScaling:FallbackThreshold", DefaultFallbackThreshold);
        var cooldown = GetSeconds("AutoScaling:CooldownMinutes", DefaultCooldown, minutes: true);

        var now = DateTimeOffset.UtcNow;
        var usage = await usageLedger.QueryAsync(new UsageQuery(FromUtc: now - lookback), cancellationToken);

        var pressured = usage
            .GroupBy(row => row.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Model = group.Key, FallbackRequests = group.Sum(row => row.FallbackRequests) })
            .Where(row => row.FallbackRequests >= threshold)
            .OrderByDescending(row => row.FallbackRequests)
            .ToArray();

        if (pressured.Length == 0)
        {
            return;
        }

        var inventory = registry.ModelInventory();
        var snapshots = registry.Snapshot(now)
            .ToDictionary(n => n.NodeId, StringComparer.OrdinalIgnoreCase);

        foreach (var row in pressured)
        {
            // One node per model per tick: if fallback pressure is still elevated next tick, that
            // is this tick's evidence the first move was not enough, not a reason to have moved
            // faster — the same hysteresis NodeReaper's fixed interval gives eviction.
            var candidate = FindScaleOutCandidate(row.Model, inventory, snapshots, cooldown, now);

            if (candidate is null)
            {
                continue;
            }

            var (node, precheck) = candidate.Value;

            if (dryRun)
            {
                logger.LogInformation(
                    "Auto-scaler (dry-run): would enable '{Model}' on node {NodeId} ({FallbackRequests} fallback requests in the last {LookbackMinutes}m; {Reason})",
                    row.Model, node.NodeId, row.FallbackRequests, lookback.TotalMinutes, precheck.Reason);
                continue;
            }

            var outcome = await toggle.SetEnabledAsync(node, row.Model, enabled: true, force: false, by: "auto-scaler", cancellationToken);

            if (outcome.Applied)
            {
                lastActionUtc[(node.NodeId, row.Model)] = now;
                logger.LogWarning(
                    "Auto-scaler enabled '{Model}' on node {NodeId} ({FallbackRequests} fallback requests in the last {LookbackMinutes}m)",
                    row.Model, node.NodeId, row.FallbackRequests, lookback.TotalMinutes);
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
