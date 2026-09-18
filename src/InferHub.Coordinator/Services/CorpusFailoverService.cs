using System.Collections.Concurrent;
using InferHub.Coordinator.Vector;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 77. Watches every node-owned collection that has a standby assigned
/// (<see cref="CollectionOwnership.StandbyAssignments"/>); once the owner has been absent from
/// <see cref="INodeRegistry"/> for longer than <c>CorpusFailover:GraceMinutes</c>, promotes the
/// standby via <see cref="NodeCorpusReplicator.PromoteAsync"/>. Same <see cref="BackgroundService"/>
/// shape as <c>AutoScalerService</c>, and the same reason for the grace period: phase 69 D5's rule
/// that a bare disconnect is not evidence of anything, only a *confirmed*, sustained absence is.
/// </summary>
/// <remarks>
/// <b>No un-promotion, ever, from this service.</b> Once a promotion is sent it is not retried and
/// not reversed here — a primary that reconnects after its standby was promoted is exactly the
/// stray-copy case phase-77's brief names as a non-goal, left for an admin to resolve by hand.
/// </remarks>
public sealed class CorpusFailoverService(
    CollectionOwnership ownership,
    INodeRegistry registry,
    NodeCorpusReplicator replicator,
    IConfiguration configuration,
    ILogger<CorpusFailoverService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>Per collection, when its owner was first observed absent. Cleared the moment the owner is seen connected again, or once a promotion has been sent for it.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> firstAbsentUtc = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("CorpusFailover:Enabled", false))
        {
            logger.LogInformation("Corpus failover disabled (CorpusFailover:Enabled is not true)");
            return;
        }

        var interval = GetSeconds("CorpusFailover:IntervalSeconds", DefaultInterval);
        var grace = GetSeconds("CorpusFailover:GraceMinutes", DefaultGrace, minutes: true);

        logger.LogInformation(
            "Corpus failover starting: interval {IntervalSeconds}s, grace {GraceMinutes}m",
            interval.TotalSeconds, grace.TotalMinutes);

        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                await TickAsync(grace, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Corpus failover tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var connected = registry.Snapshot(now)
            .Select(n => n.NodeId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var watched = ownership.StandbyAssignments();
        var stillWatching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (collection, (owner, _)) in watched)
        {
            if (!owner.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
            {
                continue; // ownership moved back to the hub since the standby was assigned; nothing to fail over
            }

            var ownerNodeId = owner["node:".Length..];
            stillWatching.Add(collection);

            if (connected.Contains(ownerNodeId))
            {
                firstAbsentUtc.TryRemove(collection, out _);
                continue;
            }

            var since = firstAbsentUtc.GetOrAdd(collection, now);

            if (now - since < grace)
            {
                continue;
            }

            firstAbsentUtc.TryRemove(collection, out _);
            await replicator.PromoteAsync(collection, cancellationToken);
        }

        // A collection dropped from the watch list (standby cleared, or promoted already) stops
        // being timed — otherwise a re-assigned standby years later would inherit a stale clock.
        foreach (var key in firstAbsentUtc.Keys.ToArray())
        {
            if (!stillWatching.Contains(key))
            {
                firstAbsentUtc.TryRemove(key, out _);
            }
        }
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
