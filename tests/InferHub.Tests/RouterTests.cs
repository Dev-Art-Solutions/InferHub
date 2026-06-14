using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

public class RouterTests
{
    [Fact]
    public void RouteReturnsNullWhenNoNodeHasModel()
    {
        var registry = new NodeRegistry();
        var router = new Router(registry);

        var node = router.Route("llama3");

        Assert.Null(node);
    }

    [Fact]
    public void RouteRoundRobinsAcrossNodesWithModel()
    {
        var registry = new NodeRegistry();
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

        var router = new Router(registry);

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

    private static NodeRegistration Registration(string nodeId, string name)
    {
        return new NodeRegistration(
            nodeId,
            name,
            "http://localhost:11434/",
            "0.4.0");
    }
}
