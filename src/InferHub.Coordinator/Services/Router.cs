using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class Router(
    INodeRegistry registry,
    IConversationAffinity affinity,
    IOptions<RouterOptions> options) : IRouter
{
    private int cursor;

    public RoutableNode? Route(string model, string? conversationKey = null)
    {
        var candidates = registry.FindNodesWithModel(model);

        if (candidates.Count == 0)
        {
            return null;
        }

        var loads = candidates
            .Select(node => (Node: node, Load: registry.GetLocalInFlight(node.ConnectionId)))
            .ToArray();

        var tieBreaker = unchecked(Interlocked.Increment(ref cursor) - 1);
        var leastBusy = PickLeastBusy(loads, tieBreaker);

        if (!string.IsNullOrEmpty(conversationKey))
        {
            var stickyConnectionId = affinity.GetNodeFor(conversationKey);

            if (stickyConnectionId is not null)
            {
                var sticky = Array.Find(loads, l => l.Node.ConnectionId == stickyConnectionId);

                if (sticky.Node is not null)
                {
                    var minLoad = loads.Min(l => l.Load);
                    var threshold = Math.Max(0, options.Value.AffinityLoadBreakThreshold);

                    if (sticky.Load - minLoad <= threshold)
                    {
                        affinity.Record(conversationKey, sticky.Node.ConnectionId);
                        return sticky.Node;
                    }
                }
            }

            affinity.Record(conversationKey, leastBusy.ConnectionId);
        }

        return leastBusy;
    }

    private static RoutableNode PickLeastBusy((RoutableNode Node, int Load)[] loads, int tieBreaker)
    {
        var minLoad = loads.Min(l => l.Load);
        var tied = loads.Where(l => l.Load == minLoad).Select(l => l.Node).ToArray();
        var index = (int)((uint)tieBreaker % tied.Length);
        return tied[index];
    }
}
