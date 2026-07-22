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
    TimeProvider time) : BackgroundService
{
    private readonly ClusterOptions options = clusterOptions.Value;

    /// <summary>Local clock, set on every successful renewal. The fence deadline is measured from it.</summary>
    private DateTimeOffset lastProofUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Cluster mode is on; instance {InstanceId} is contending for lease '{LeaseName}' (ttl {Ttl}s, renew {Renew}s)",
            options.InstanceId,
            options.LeaseName,
            options.LeaseTtlSeconds,
            options.RenewIntervalSeconds);

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

        await ReleaseIfActiveAsync();
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
