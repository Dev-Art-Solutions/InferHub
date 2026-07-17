using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class Router(
    INodeRegistry registry,
    IConversationAffinity affinity,
    ThroughputTracker throughput,
    IOptions<RouterOptions> options) : IRouter
{
    private int cursor;

    public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null)
    {
        var candidates = registry.FindNodesWithModel(model);

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
            var stickyConnectionId = affinity.GetNodeFor(conversationKey);

            if (stickyConnectionId is not null)
            {
                var sticky = Array.Find(loads, l => l.Node.ConnectionId == stickyConnectionId);

                if (sticky.Node is not null)
                {
                    // Affinity still wins on load headroom — a warm model on a slower node usually
                    // beats a cold one on a faster node, which is exactly what affinity encodes.
                    var minLoad = loads.Min(l => l.Load);
                    var threshold = Math.Max(0, options.Value.AffinityLoadBreakThreshold);

                    if (sticky.Load - minLoad <= threshold)
                    {
                        affinity.Record(conversationKey, sticky.Node.ConnectionId);
                        return sticky.Node;
                    }
                }
            }

            affinity.Record(conversationKey, best.ConnectionId);
        }

        return best;
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
