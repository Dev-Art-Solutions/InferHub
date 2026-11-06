using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Cluster;

/// <summary>
/// Acquire, renew, and — the part that matters — <b>fence</b>. Registered only when
/// <c>Cluster:Enabled=true</c>.
/// </summary>
/// <remarks>
/// The split-brain guard is deliberately local. A partitioned active coordinator cannot ask
/// anyone whether it still leads: by definition it cannot reach the database that knows. So the
/// rule is not "demote when told to" but "demote when <i>this</i> instance has not <i>proved</i>
/// leadership within the TTL", measured on our own clock from the last successful renewal. That is
/// the same deadline the database will use to hand the lease to the standby, so the two windows
/// cannot overlap in a way that has both hubs serving.
///
/// The consequence is that an unreachable database demotes a healthy primary after one TTL. That
/// is correct and not a bug to soften: an inference request the mesh cannot attribute to a single
/// leader is worse than a 503 that a load balancer routes elsewhere.
/// </remarks>
internal sealed class ClusterLeaseService(
    IClusterLease lease,
    ClusterMembership membership,
    IOptions<ClusterOptions> clusterOptions,
    INodeConnectionTracker connections,
    ILogger<ClusterLeaseService> logger,
    TimeProvider time,
    INodeRegistry? registry = null,
    ClusterPeers? peers = null) : BackgroundService
{
    private readonly ClusterOptions options = clusterOptions.Value;

    /// <summary>Local clock, set on every successful renewal. The fence deadline is measured from it.</summary>
    private DateTimeOffset lastProofUtc = DateTimeOffset.MinValue;

    // Phase 131: since when the preferred instance has been alive without a break, as this instance has seen it.
    private DateTimeOffset? preferredAliveSince;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ActiveActive)
        {
            logger.LogInformation(
                "Cluster mode is on, active-active; instance {InstanceId} serves alongside the other members of '{LeaseName}' and is reached at {Url} (ttl {Ttl}s, renew {Renew}s)",
                options.InstanceId,
                options.LeaseName,
                options.AdvertiseUrl,
                options.LeaseTtlSeconds,
                options.RenewIntervalSeconds);
        }
        else
        {
            logger.LogInformation(
                "Cluster mode is on; instance {InstanceId} is contending for lease '{LeaseName}' (ttl {Ttl}s, renew {Renew}s)",
                options.InstanceId,
                options.LeaseName,
                options.LeaseTtlSeconds,
                options.RenewIntervalSeconds);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);

            try
            {
                // Never sleep past the fence deadline: tick granularity must not add slack to the
                // one number this whole design promises to respect.
                var delay = RemainingBeforeFence() is { } remaining && remaining < options.RenewInterval
                    ? remaining
                    : options.RenewInterval;

                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!options.ActiveActive)
        {
            await ReleaseIfActiveAsync();
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        // Check the deadline BEFORE doing any I/O, and bound the attempt by what is left of it.
        // Found live in phase 32: against an unreachable database the round-trip itself burns
        // Npgsql's connect timeout, so demotion landed at ~23s on a 15s TTL — and the lease row
        // frees at 15s. That gap is a window in which the standby has taken the lease and the old
        // primary still believes it leads: the exact split brain the fence exists to prevent. The
        // fence deadline is only safe if nothing we do can overrun it.
        if (FenceIfDeadlinePassed())
        {
            return;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (RemainingBeforeFence() is { } remaining)
        {
            attempt.CancelAfter(remaining);
        }

        try
        {
            // Phase 132 D1: an active-active member holds no lease. Its own heartbeat is its proof, and the fence below is the
            // same: a member that cannot write it within the TTL stops serving and lets its nodes go to another.
            if (options.ActiveActive)
            {
                await ServeAlongsideAsync(attempt.Token);
                return;
            }

            // Phase 131 D4: failback. Every member says it is alive; one that is not preferred stands aside while the preferred
            // one is alive, and an active one hands the lease back once the preferred has been alive long enough.
            await lease.HeartbeatAsync(attempt.Token);

            if (await StandAsideForPreferredAsync(attempt.Token))
            {
                return;
            }

            var result = await lease.TryAcquireOrRenewAsync(attempt.Token);

            if (result.Held)
            {
                lastProofUtc = time.GetUtcNow();

                if (membership.MarkActive(result.Fence, lastProofUtc))
                {
                    logger.LogInformation(
                        "Promoted to ACTIVE coordinator (lease '{LeaseName}', fence {Fence}). Nodes may now register.",
                        options.LeaseName,
                        result.Fence);
                }

                return;
            }

            Demote($"the lease is held by '{result.Holder}' until {result.ExpiresAtUtc:O}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The database is unreachable, which is "unknown", not "lost" — but the fence deadline
            // does not care why we failed to prove leadership, only that we did not. A cancelled
            // attempt lands here too: that is the deadline arriving mid-round-trip.
            if (FenceIfDeadlinePassed(ex))
            {
                return;
            }

            logger.LogWarning(ex, "Coordinator lease round-trip failed; retrying in {Delay}s", options.RenewIntervalSeconds);
        }
    }

    /// <summary>Time left before this instance must stop believing it leads, or null if it does not.</summary>
    /// <summary>
    /// True when this instance must not lead this tick: it is not the preferred instance and the preferred one is alive. A
    /// standby then does not contend; an active one keeps leading until the preferred has been alive for
    /// <c>FailbackAfterSeconds</c> without a break, then releases the lease and steps down, so the preferred one takes it on
    /// its next tick. Leadership moves once, on purpose, never back and forth on a flapping link.
    /// </summary>
    private async Task<bool> StandAsideForPreferredAsync(CancellationToken cancellationToken)
    {
        var preferred = options.PreferredInstance?.Trim();

        if (string.IsNullOrEmpty(preferred) || string.Equals(preferred, options.InstanceId, StringComparison.Ordinal))
        {
            return false;
        }

        var alive = await lease.SinceSeenAsync(preferred, cancellationToken) is { } since && since < options.LeaseTtl;
        var now = time.GetUtcNow();

        if (!alive)
        {
            preferredAliveSince = null;
            return false;
        }

        preferredAliveSince ??= now;

        if (membership.Role is not ClusterRole.Active)
        {
            membership.MarkStandby($"the preferred coordinator '{preferred}' is alive; this instance stands by for it");
            return true;
        }

        if (now - preferredAliveSince.Value < options.FailbackAfter)
        {
            return false;
        }

        await lease.ReleaseAsync(cancellationToken);
        Demote($"failing back: the preferred coordinator '{preferred}' has been alive for {options.FailbackAfterSeconds}s");
        preferredAliveSince = null;
        return true;
    }

    private async Task ServeAlongsideAsync(CancellationToken cancellationToken)
    {
        var models = ClusterPeers.Describe(registry?.AdvertisedModels() ?? []);
        await lease.AdvertiseAsync(options.AdvertiseUrl!.Trim(), models, cancellationToken);
        lastProofUtc = time.GetUtcNow();

        var rows = await lease.MembersAsync(cancellationToken);
        peers?.Replace(rows, options.InstanceId, options.LeaseTtl);

        var others = rows
            .Where(row => !string.Equals(row.Instance, options.InstanceId, StringComparison.Ordinal) && row.SeenAgo < options.LeaseTtl)
            .Select(row => row.Instance)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var detail = others.Length == 0
            ? "serving (active-active); no other member is alive"
            : $"serving alongside {string.Join(", ", others)} (active-active)";

        if (membership.MarkActive(0, lastProofUtc, detail))
        {
            logger.LogInformation("Serving as an ACTIVE-ACTIVE member of '{LeaseName}'. Nodes may now register: {Detail}", options.LeaseName, detail);
        }
    }

    private TimeSpan? RemainingBeforeFence()
    {
        if (membership.Role is not ClusterRole.Active)
        {
            return null;
        }

        var remaining = options.LeaseTtl - (time.GetUtcNow() - lastProofUtc);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private bool FenceIfDeadlinePassed(Exception? cause = null)
    {
        if (membership.Role is not ClusterRole.Active)
        {
            return false;
        }

        var sinceProof = time.GetUtcNow() - lastProofUtc;

        if (sinceProof < options.LeaseTtl)
        {
            return false;
        }

        Demote($"the lease could not be renewed for {sinceProof.TotalSeconds:F0}s (ttl {options.LeaseTtlSeconds}s)");
        logger.LogError(cause, "Fenced: demoted to standby after failing to renew the coordinator lease");
        return true;
    }

    private void Demote(string reason)
    {
        if (!membership.MarkStandby(reason))
        {
            return;
        }

        logger.LogWarning("Demoted to STANDBY: {Reason}", reason);

        // Phase 132: a member that stopped serving passes nothing on either.
        peers?.Clear();

        // Cut the fleet loose rather than leaving it pointed at a hub that will refuse its work.
        // Nodes reconnect through their endpoint list and land on whoever now holds the lease; a
        // node left attached to a standby is a node the mesh has silently lost.
        var dropped = connections.AbortAll();

        if (dropped > 0)
        {
            logger.LogInformation("Dropped {Count} node connection(s) so they fail over to the active coordinator", dropped);
        }
    }

    internal async Task ReleaseIfActiveAsync()
    {
        if (membership.Role is not ClusterRole.Active)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await lease.ReleaseAsync(timeout.Token);
            logger.LogInformation("Released the coordinator lease on shutdown");
        }
        catch (Exception ex)
        {
            // Best effort: the standby will take it on expiry, which is what the TTL is for.
            logger.LogWarning(ex, "Could not release the coordinator lease on shutdown");
        }
    }
}
