using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about its tool runtime (phase 45). In memory, like every other
/// registry on the hub (rule 4) — a coordinator restart forgets it and every node re-reports on its
/// next model refresh.
/// </summary>
/// <remarks>
/// <b>Nothing here ever queries a node.</b> Phase-44 D6's mailbox, reused rather than re-argued: the
/// hub records what arrives and answers <c>/api/status</c> and <c>/metrics</c> from it, because a
/// console that dials the fleet cannot show you a node that has stopped answering.
/// </remarks>
public sealed class NodeToolRegistry
{
    private readonly ConcurrentDictionary<string, NodeToolState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeToolState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeToolState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public IReadOnlyCollection<NodeToolState> All() =>
        states.Values.OrderBy(state => state.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}
