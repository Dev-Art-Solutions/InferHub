using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 128. What each sealed node's own egress check found, by node id — the 44 D6 mailbox again. Stored under the node
/// id the connection registered as, never the one in the report, so a node cannot speak for another.
/// </summary>
public sealed class NodeEgressRegistry(ILogger<NodeEgressRegistry> logger)
{
    private readonly ConcurrentDictionary<string, NodeEgressState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(string nodeId, string? name, NodeEgressState state)
    {
        var stored = state with { NodeId = nodeId };
        var previous = states.GetValueOrDefault(nodeId);
        states[nodeId] = stored;

        if (stored.Open.Count > 0 && (previous is null || !previous.Open.SequenceEqual(stored.Open)))
        {
            logger.LogWarning(
                "Sealed node {NodeId} ({Name}) can reach {Open} itself: its operating system lets traffic out that does not go through this coordinator",
                nodeId,
                name,
                string.Join(", ", stored.Open));
        }
    }

    public NodeEgressState? Of(string nodeId) => states.GetValueOrDefault(nodeId);

    public IReadOnlyList<NodeEgressState> All() => states.Values.ToArray();

    public void Forget(string nodeId) => states.TryRemove(nodeId, out _);
}
