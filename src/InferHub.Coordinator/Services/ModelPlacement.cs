using InferHub.Coordinator.Vector;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The pure decision behind <c>POST /api/admin/models/{model}/ensure</c> (phase 26): given the
/// current fleet and who already holds a model, which nodes should be pulled onto to reach the
/// requested replica count. Kept free of HTTP/registry so it is testable in isolation and so the
/// endpoint is only glue. It reuses <see cref="ReplicaPlacement"/> — the same phase-15 heuristic —
/// rather than inventing a second placement algorithm.
/// </summary>
internal static class ModelPlacement
{
    public sealed record Plan(
        IReadOnlyList<string> PullConnectionIds,
        int AlreadyPresent,
        int EffectiveTarget,
        int NonManageableHolders,
        int EligibleCandidates,
        bool Satisfied,
        int Shortfall);

    /// <param name="nodes">Every connected node.</param>
    /// <param name="holderConnectionIds">Connection ids of non-cordoned nodes that already hold the model.</param>
    /// <param name="replicas">Desired replica count (>= 1).</param>
    public static Plan Choose(
        IReadOnlyCollection<NodeSnapshot> nodes,
        IReadOnlySet<string> holderConnectionIds,
        int replicas)
    {
        var byConn = nodes.ToDictionary(n => n.ConnectionId, StringComparer.Ordinal);

        // A holder we cannot pull onto (e.g. a vLLM node serving the model) still counts toward N.
        var nonManageableHolders = holderConnectionIds.Count(id =>
            byConn.TryGetValue(id, out var s) && !s.SupportsModelManagement);
        var effectiveTarget = Math.Max(0, replicas - nonManageableHolders);

        var candidates = nodes
            .Where(n => !n.Cordoned && n.SupportsModelManagement)
            .OrderBy(n => n.LocalInFlight)
            .ThenBy(n => n.NodeId, StringComparer.OrdinalIgnoreCase)
            .Select(n => new NodeCandidate(n.ConnectionId, n.NodeId, n.LocalInFlight))
            .ToArray();

        var manageableHolders = candidates
            .Where(c => holderConnectionIds.Contains(c.ConnectionId))
            .Select(c => c.ConnectionId)
            .ToHashSet(StringComparer.Ordinal);

        var target = ReplicaPlacement.ComputeTarget(candidates, manageableHolders, effectiveTarget);
        var toPull = target.Where(id => !manageableHolders.Contains(id)).ToArray();

        var placed = holderConnectionIds.Count + toPull.Length;
        var satisfied = placed >= replicas;

        return new Plan(
            toPull,
            holderConnectionIds.Count,
            effectiveTarget,
            nonManageableHolders,
            candidates.Length,
            satisfied,
            satisfied ? 0 : replicas - placed);
    }
}
