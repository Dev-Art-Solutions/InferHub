using System.Collections.Concurrent;
using InferHub.Shared.Fit;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about the slice it offers groups (phase 134, D5) — phase 45's registry once more. In
/// memory (rule 4): a coordinator restart forgets it and every node that offers one re-reports at registration. A node
/// that offers nothing never reports, so it has no row.
/// </summary>
public sealed class NodeShardRegistry
{
    private readonly ConcurrentDictionary<string, NodeShardState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeShardState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeShardState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}
