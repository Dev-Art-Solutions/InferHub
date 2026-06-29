namespace InferHub.Coordinator.Vector;

internal static class ReplicaPlacement
{
    /// <summary>
    /// Compute the set of node connection IDs that should hold a replica.
    /// Prefers nodes already holding the replica, then fills the remaining slots from
    /// the supplied candidate ordering (least-busy first). The hub-local index is always
    /// implicit; this only places node-side replicas.
    /// </summary>
    public static IReadOnlyList<string> ComputeTarget(
        IReadOnlyList<NodeCandidate> orderedCandidates,
        IReadOnlySet<string> currentHolders,
        int targetCount)
    {
        if (targetCount < 1 || orderedCandidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        var cap = Math.Min(targetCount, orderedCandidates.Count);

        // Preserve already-placed replicas at the head of the list; fills with new ones
        // from the remaining candidates so we don't move data around needlessly.
        var keep = orderedCandidates
            .Where(c => currentHolders.Contains(c.ConnectionId))
            .Take(cap)
            .ToList();

        if (keep.Count < cap)
        {
            var heldIds = new HashSet<string>(keep.Select(k => k.ConnectionId), StringComparer.Ordinal);
            foreach (var candidate in orderedCandidates)
            {
                if (heldIds.Contains(candidate.ConnectionId)) continue;
                keep.Add(candidate);
                if (keep.Count == cap) break;
            }
        }

        return keep.Select(k => k.ConnectionId).ToArray();
    }
}

internal sealed record NodeCandidate(string ConnectionId, string NodeId, int Load);
