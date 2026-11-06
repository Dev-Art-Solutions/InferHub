using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Cluster;

/// <summary>
/// Phase 132 D5. A revocation drops the node at once (127 D5) — on the member that revoked it. In an active-active cluster the
/// node may be connected to another member, which never saw the request. Each member therefore looks at its own nodes every
/// renew interval and drops one whose certificates are all revoked in the shared record. Registered only in active-active
/// with node certificates on.
/// </summary>
internal sealed class ClusterRevocationSweep(
    NodeCertificateAuthority authority,
    INodeRegistry registry,
    INodeConnectionTracker connections,
    IOptions<ClusterOptions> options,
    ILogger<ClusterRevocationSweep> logger,
    TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not compare this member's nodes with the shared certificate record; trying again");
            }

            try
            {
                await Task.Delay(options.Value.RenewInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Drops every connected node whose certificates are all revoked; returns how many.</summary>
    internal int Sweep()
    {
        var dropped = 0;

        foreach (var node in registry.Snapshot(time.GetUtcNow()))
        {
            if (authority.IsRevoked(node.NodeId) && connections.Abort(node.ConnectionId))
            {
                dropped++;
                logger.LogWarning("Dropped node {Node} ({NodeId}): its certificate was revoked on another member", node.Name, node.NodeId);
            }
        }

        return dropped;
    }
}
