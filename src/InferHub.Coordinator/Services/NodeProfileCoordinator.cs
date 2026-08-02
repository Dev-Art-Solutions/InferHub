using InferHub.Coordinator.Hubs;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The wire half of node profiles (phase 43): it decides which connected nodes a write affects and
/// sends each of them their assignment, down the outbound SignalR connection the node opened.
/// </summary>
/// <remarks>
/// <para>
/// The split is <see cref="ModelCommandCoordinator"/>'s: <see cref="IProfileRegistry"/> is the book
/// and knows nothing about SignalR; this knows how to reach a node and nothing about what a profile
/// means.
/// </para>
/// <para>
/// <b>It only ever pushes.</b> The pull direction — a node asking for its profile at registration —
/// is <c>NodeHub.RequestNodeProfile</c>, and it exists so that a hub does not have to remember which
/// nodes have which revision. Together they are why a rebooted node converges with no operator
/// action: whichever end notices first, the answer is the same document.
/// </para>
/// </remarks>
public sealed class NodeProfileCoordinator(
    IHubContext<NodeHub> hubContext,
    INodeRegistry registry,
    IProfileRegistry profiles,
    ILogger<NodeProfileCoordinator> logger)
{
    /// <summary>
    /// Re-evaluates every connected node and sends whatever changed for it. Called after a write or
    /// a delete, because a selector edit can pull one node in and push another out in the same
    /// request.
    /// </summary>
    public async Task<IReadOnlyList<ProfilePush>> ReassertAsync(CancellationToken cancellationToken)
    {
        var pushes = new List<ProfilePush>();

        foreach (var node in registry.Snapshot(DateTimeOffset.UtcNow))
        {
            var assignment = profiles.MatchFor(node.NodeId, node.Labels);

            if (assignment.IsConflict)
            {
                // Deliberately send nothing: the node keeps its last applied profile and the
                // operator sees `conflict` on /api/status (D4).
                logger.LogWarning(
                    "Node {NodeId} matches {Count} profiles ({Profiles}); sending none until the selectors are fixed",
                    node.NodeId,
                    assignment.Conflicts!.Count,
                    string.Join(", ", assignment.Conflicts!));

                pushes.Add(new ProfilePush(node.NodeId, null, assignment.Conflicts));
                continue;
            }

            await SendAsync(node.ConnectionId, node.NodeId, assignment.Profile, cancellationToken);
            pushes.Add(new ProfilePush(node.NodeId, assignment.Profile?.Name, null));
        }

        return pushes;
    }

    private async Task SendAsync(
        string connectionId,
        string nodeId,
        NodeProfile? profile,
        CancellationToken cancellationToken)
    {
        try
        {
            if (profile is null)
            {
                // Distinct from "no message": a node whose profile was deleted has to be told to
                // revert to its own configuration, or it would keep serving a narrowed fleet
                // forever and nothing on either side would say why.
                await hubContext.Clients.Client(connectionId).SendAsync("ClearNodeProfile", cancellationToken);
                logger.LogInformation("Cleared the node profile on {NodeId}", nodeId);
                return;
            }

            await hubContext.Clients.Client(connectionId).SendAsync("ApplyNodeProfile", profile, cancellationToken);

            logger.LogInformation(
                "Sent profile '{Profile}' revision {Revision} to node {NodeId}",
                profile.Name,
                profile.Revision,
                nodeId);
        }
        catch (Exception ex)
        {
            // A node that missed the push asks for its profile the next time it registers, so a
            // failure here is a delay rather than a divergence — which is the whole point of
            // desired state over commands (D2).
            logger.LogWarning(ex, "Could not send a profile to node {NodeId}; it will converge on its next registration", nodeId);
        }
    }
}

/// <summary>What a re-assert did for one node, so the admin response can say so.</summary>
public sealed record ProfilePush(string NodeId, string? Profile, IReadOnlyList<string>? Conflicts);
