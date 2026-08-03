using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// The last thing each node said about its corpus (phase 44, D6). In memory, like every other
/// registry on the hub (rule 4) — a coordinator restart forgets it and every node re-reports on its
/// next model refresh.
/// </summary>
/// <remarks>
/// <b>Nothing here ever queries a node.</b> This is a mailbox, not a client: the hub records what
/// arrives and answers <c>/api/status</c> from it, because a status page that dials the fleet cannot
/// answer when the fleet is what is broken.
/// </remarks>
public sealed class NodeCorpusRegistry
{
    private readonly ConcurrentDictionary<string, NodeCorpusState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeCorpusState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeCorpusState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public IReadOnlyCollection<NodeCorpusState> All() =>
        states.Values.OrderBy(state => state.NodeId, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}
