using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class Router(
    INodeRegistry registry,
    IConversationAffinity affinity,
    ThroughputTracker throughput,
    IOptions<RouterOptions> options) : IRouter
{
    private int cursor;

    public RoutableNode? Route(
        string model,
        string? conversationKey = null,
        string? excludeConnectionId = null,
        string? capability = null,
        bool requireStreamedAttachments = false,
        bool requireStreamedSpeech = false)
    {
        // Phase 40: the capability filter runs first and everything after it — least-busy,
        // throughput, sticky affinity, the cordon skip — is untouched. A node that cannot do this
        // kind of work is not a candidate to be balanced against. Phase 53 narrows the same way and
        // for the same reason: a node that cannot pull a stream cannot serve a streamed job.
        var candidates = registry.FindNodesWithModel(
            model,
            capability,
            requireStreamedAttachments,
            requireStreamedSpeech: requireStreamedSpeech);

        if (!string.IsNullOrEmpty(excludeConnectionId))
        {
            candidates = candidates
                .Where(node => !string.Equals(node.ConnectionId, excludeConnectionId, StringComparison.Ordinal))
                .ToArray();
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Phase 86 D3. Everything below — least-busy, throughput, affinity — now runs over the best
        // non-empty tier instead of every holder. With no on-demand node among the candidates there
        // is one tier and this is the identity, which is the whole byte-identity argument.
        candidates = WarmestTier(candidates, capability);

        var loads = candidates
            .Select(node => (Node: node, Load: registry.GetLocalInFlight(node.ConnectionId)))
            .ToArray();

        var tieBreaker = unchecked(Interlocked.Increment(ref cursor) - 1);

        // The "best" non-sticky pick depends on the strategy. least-busy is the default and is
        // bit-for-bit the pre-v2.8 behaviour; throughput weighs measured tokens/sec against load.
        var best = string.Equals(options.Value.Strategy, RouterOptions.StrategyThroughput, StringComparison.OrdinalIgnoreCase)
            ? PickByThroughput(loads, model, tieBreaker)
            : PickLeastBusy(loads, tieBreaker);

        if (!string.IsNullOrEmpty(conversationKey))
        {
            // Affinity keys on the stable nodeId (phase 30). We resolve it to a live candidate here:
            // a sticky node that is disconnected, cordoned, or no longer holds the model is simply
            // absent from `candidates`, so a stale hint is a clean miss and we fall through to best.
            var stickyNodeId = affinity.GetNodeFor(conversationKey);

            if (stickyNodeId is not null)
            {
                var sticky = Array.Find(loads, l => string.Equals(l.Node.NodeId, stickyNodeId, StringComparison.Ordinal));

                if (sticky.Node is not null)
                {
                    // Affinity still wins on load headroom — a warm model on a slower node usually
                    // beats a cold one on a faster node, which is exactly what affinity encodes.
                    var minLoad = loads.Min(l => l.Load);
                    var threshold = Math.Max(0, options.Value.AffinityLoadBreakThreshold);

                    if (sticky.Load - minLoad <= threshold)
                    {
                        affinity.Record(conversationKey, sticky.Node.NodeId);
                        return sticky.Node;
                    }
                }
            }

            affinity.Record(conversationKey, best.NodeId);
        }

        return best;
    }

    /// <summary>
    /// Phase 86 D3: warm (no switch needed), then free (the card is idle), then cold (another service
    /// holds it, or it is mid-switch). A preference, never a filter — a cold node that is the only
    /// holder is still returned, and waits in its own arbiter exactly as it did in v3.50.
    /// </summary>
    /// <remarks>
    /// A node with no <see cref="RoutableNode.OnDemand"/> state is always warm: its card is not
    /// shared between services, so nothing about it changes. A request with no capability named is
    /// not tiered at all, because "warm for what" has no answer.
    /// </remarks>
    internal static IReadOnlyCollection<RoutableNode> WarmestTier(IReadOnlyCollection<RoutableNode> candidates, string? capability)
    {
        if (capability is null || candidates.All(node => node.OnDemand is null))
        {
            return candidates;
        }

        var warm = new List<RoutableNode>();
        var free = new List<RoutableNode>();
        var cold = new List<RoutableNode>();

        foreach (var node in candidates)
        {
            switch (Temperature(node.OnDemand, capability))
            {
                case 0: warm.Add(node); break;
                case 1: free.Add(node); break;
                default: cold.Add(node); break;
            }
        }

        return warm.Count > 0 ? warm : free.Count > 0 ? free : cold;
    }

    private static int Temperature(Shared.Contracts.OnDemandState? state, string capability)
    {
        if (state is null)
        {
            return 0;
        }

        if (state.Switching)
        {
            return 2;
        }

        if (state.Holder is null)
        {
            return 1;
        }

        return state.WarmFor.Contains(capability, StringComparer.OrdinalIgnoreCase) ? 0 : 2;
    }

    private static RoutableNode PickLeastBusy((RoutableNode Node, int Load)[] loads, int tieBreaker)
    {
        var minLoad = loads.Min(l => l.Load);
        var tied = loads.Where(l => l.Load == minLoad).Select(l => l.Node).ToArray();
        var index = (int)((uint)tieBreaker % tied.Length);
        return tied[index];
    }

    // Pick the node with the best expected completion time: (load + 1) / tokens-per-second.
    // An unmeasured node is treated as *average* (the mean measured rate for this model), never
    // as slow — otherwise a fresh node never gets a request and never earns a measurement (D4).
    private RoutableNode PickByThroughput((RoutableNode Node, int Load)[] loads, string model, int tieBreaker)
    {
        var average = throughput.AverageForModel(model);

        // Nothing measured yet anywhere → there is no signal to route on; fall back to least-busy.
        if (average is null)
        {
            return PickLeastBusy(loads, tieBreaker);
        }

        var scored = loads
            .Select(l =>
            {
                var rate = throughput.GetTokensPerSecond(l.Node.NodeId, model) ?? average.Value;
                if (rate <= 0) rate = average.Value;
                var expectedTime = (l.Load + 1) / rate; // lower is better
                return (l.Node, ExpectedTime: expectedTime);
            })
            .ToArray();

        var bestTime = scored.Min(s => s.ExpectedTime);
        // Ties (e.g. all unmeasured → identical expected time) rotate via the cursor, so an
        // all-unmeasured fleet still round-robins instead of pinning one node.
        var tied = scored.Where(s => s.ExpectedTime <= bestTime + 1e-9).Select(s => s.Node).ToArray();
        var index = (int)((uint)tieBreaker % tied.Length);
        return tied[index];
    }
}
