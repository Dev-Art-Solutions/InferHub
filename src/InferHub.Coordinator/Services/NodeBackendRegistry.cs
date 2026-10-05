using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about its engines (phase 95) — phase 45's registry, one report
/// over. In memory, like every other registry on the hub (rule 4): a coordinator restart forgets it
/// and every node re-reports on its next model refresh.
/// </summary>
/// <remarks>
/// <b>Nothing here ever queries a node.</b> Phase-44 D6's mailbox, reused rather than re-argued: the
/// hub records what arrives and answers <c>/api/status</c> from it, because a
/// console that dials the fleet cannot show you a node that has stopped answering.
/// </remarks>
public sealed class NodeBackendRegistry
{
    private readonly ConcurrentDictionary<string, NodeBackendState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeBackendState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeBackendState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public IReadOnlyCollection<NodeBackendState> All() =>
        states.Values.OrderBy(state => state.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}
