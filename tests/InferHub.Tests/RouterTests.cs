using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class RouterTests
{
    [Fact]
    public void RouteReturnsNullWhenNoNodeHasModel()
    {
        var registry = new NodeRegistry();
        var router = NewRouter(registry, out _);

        var node = router.Route("llama3");

        Assert.Null(node);
    }

    [Fact]
    public void RouteRoundRobinsAcrossEquallyLoadedNodes()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        var router = NewRouter(registry, out _);

        var first = router.Route("llama3");
        var second = router.Route("llama3");
        var third = router.Route("llama3");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal("node-a", first.NodeId);
        Assert.Equal("node-b", second.NodeId);
        Assert.Equal("node-a", third.NodeId);
    }

    [Fact]
    public void RoutePrefersLeastBusyNode()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        // pin node-a as busy
        registry.IncrementInFlight("connection-a");
        registry.IncrementInFlight("connection-a");

        var router = NewRouter(registry, out _);

        var first = router.Route("llama3");
        var second = router.Route("llama3");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("node-b", first.NodeId);
        Assert.Equal("node-b", second.NodeId);
    }

    [Fact]
    public void RouteStaysOnAffinityNodeAcrossTurns()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        var router = NewRouter(registry, out _);

        var first = router.Route("llama3", "conversation-1");
        var second = router.Route("llama3", "conversation-1");
        var third = router.Route("llama3", "conversation-1");

        Assert.NotNull(first);
        Assert.Equal(first.NodeId, second?.NodeId);
        Assert.Equal(first.NodeId, third?.NodeId);
    }

    [Fact]
    public void RouteFallsBackWhenAffinityNodeIsGone()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        var router = NewRouter(registry, out _);

        var first = router.Route("llama3", "conversation-1");
        Assert.NotNull(first);

        var stickyConnection = first.NodeId == "node-a" ? "connection-a" : "connection-b";
        registry.Remove(stickyConnection);

        var second = router.Route("llama3", "conversation-1");

        Assert.NotNull(second);
        Assert.NotEqual(first.NodeId, second.NodeId);
    }

    [Fact]
    public void RouteBreaksAffinityWhenStickyNodeIsFarBusier()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        var router = NewRouter(registry, out _, threshold: 1);

        var first = router.Route("llama3", "conversation-1");
        Assert.NotNull(first);

        // saturate the sticky node beyond the threshold
        var stickyConnection = first.NodeId == "node-a" ? "connection-a" : "connection-b";
        registry.IncrementInFlight(stickyConnection);
        registry.IncrementInFlight(stickyConnection);
        registry.IncrementInFlight(stickyConnection);

        var second = router.Route("llama3", "conversation-1");

        Assert.NotNull(second);
        Assert.NotEqual(first.NodeId, second.NodeId);
    }

    [Fact]
    public void RouteExcludesGivenConnection()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        var router = NewRouter(registry, out _);

        var node = router.Route("llama3", conversationKey: null, excludeConnectionId: "connection-a");

        Assert.NotNull(node);
        Assert.Equal("node-b", node!.NodeId);
    }

    [Fact]
    public void RouteReturnsNullWhenAllCapableNodesAreExcluded()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-only", Registration("node-only", "only-node"), now);
        registry.ReportModels(
            "connection-only",
            new NodeModels("node-only", [new ModelInfo("llama3", "digest", 1)], now),
            now);

        var router = NewRouter(registry, out _);

        var node = router.Route("llama3", conversationKey: null, excludeConnectionId: "connection-only");

        Assert.Null(node);
    }

    [Fact]
    public void RouteSkipsCordonedNodes()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        registry.Cordon("node-a");

        var router = NewRouter(registry, out _);

        var first = router.Route("llama3");
        var second = router.Route("llama3");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("node-b", first.NodeId);
        Assert.Equal("node-b", second.NodeId);
    }

    [Fact]
    public void RouteReturnsNullWhenAllCapableNodesAreCordoned()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        registry.Cordon("node-a");
        registry.Cordon("node-b");

        var router = NewRouter(registry, out _);

        Assert.Null(router.Route("llama3"));
    }

    [Fact]
    public void UncordonRestoresEligibility()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        registry.Cordon("node-a");
        registry.Uncordon("node-a");

        var router = NewRouter(registry, out _);

        // Round-robin across both eligible nodes again.
        var first = router.Route("llama3");
        var second = router.Route("llama3");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.NodeId, second.NodeId);
    }

    [Fact]
    public void RouteSpreadsDistinctConversationsAcrossNodes()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);

        var router = NewRouter(registry, out _);

        // Each route picks least-busy and the picked node's in-flight is bumped by the caller
        // (the dispatcher in production). Simulate that by incrementing after each Route call.
        var nodeForConvOne = router.Route("llama3", "conv-1");
        Assert.NotNull(nodeForConvOne);
        registry.IncrementInFlight(nodeForConvOne.ConnectionId);

        var nodeForConvTwo = router.Route("llama3", "conv-2");
        Assert.NotNull(nodeForConvTwo);

        Assert.NotEqual(nodeForConvOne.NodeId, nodeForConvTwo.NodeId);
    }

    private static Router NewRouter(NodeRegistry registry, out ConversationAffinity affinity, int threshold = 2)
    {
        var options = Options.Create(new RouterOptions
        {
            AffinitySlidingMinutes = 10,
            AffinityLoadBreakThreshold = threshold
        });

        affinity = new ConversationAffinity(options);
        return new Router(registry, affinity, new ThroughputTracker(), options);
    }

    private static void SeedTwoNodes(NodeRegistry registry)
    {
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-b", Registration("node-b", "beta-node"), now);
        registry.Upsert("connection-a", Registration("node-a", "alpha-node"), now);
        registry.ReportModels(
            "connection-b",
            new NodeModels("node-b", [new ModelInfo("llama3", "digest-b", 200)], now),
            now);
        registry.ReportModels(
            "connection-a",
            new NodeModels("node-a", [new ModelInfo("LLAMA3", "digest-a", 100)], now),
            now);
    }

    private static NodeRegistration Registration(string nodeId, string name)
    {
        return new NodeRegistration(
            nodeId,
            name,
            "http://localhost:11434/",
            "0.7.0");
    }
}
