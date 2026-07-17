using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class ThroughputRoutingTests
{
    [Fact]
    public void FastNodeWinsOverSlowIdleNode()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        var (router, tracker) = NewRouter(registry, RouterOptions.StrategyThroughput);

        tracker.RecordFromResponse("node-a", Resp("llama3", evalCount: 1000, durationNs: 100_000_000)); // ~10000 tok/s
        tracker.RecordFromResponse("node-b", Resp("llama3", evalCount: 100, durationNs: 100_000_000));   // ~1000 tok/s

        // Both idle, so the only signal is throughput — the fast node should win every time.
        Assert.Equal("node-a", router.Route("llama3")!.NodeId);
        Assert.Equal("node-a", router.Route("llama3")!.NodeId);
        Assert.Equal("node-a", router.Route("llama3")!.NodeId);
    }

    [Fact]
    public void UnmeasuredNodeIsTreatedAsAverageNotStarved()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        var (router, tracker) = NewRouter(registry, RouterOptions.StrategyThroughput);

        // Only node-a has a measurement; node-b must be treated as average (= node-a's rate),
        // which ties them, so node-b still receives traffic rather than being frozen out.
        tracker.RecordFromResponse("node-a", Resp("llama3", evalCount: 1000, durationNs: 100_000_000));

        var picks = new HashSet<string>();
        for (var i = 0; i < 8; i++) picks.Add(router.Route("llama3")!.NodeId);

        Assert.Contains("node-a", picks);
        Assert.Contains("node-b", picks); // not starved
    }

    [Fact]
    public void AffinityStillWinsOverThroughput()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        var (router, tracker, affinity) = NewRouterWithAffinity(registry, RouterOptions.StrategyThroughput);

        tracker.RecordFromResponse("node-a", Resp("llama3", evalCount: 1000, durationNs: 100_000_000)); // fast
        tracker.RecordFromResponse("node-b", Resp("llama3", evalCount: 100, durationNs: 100_000_000));   // slow

        // Pin the conversation to the slow node; affinity should keep it there (both idle, within
        // the load-break threshold) even though node-a is faster.
        affinity.Record("conv-1", "connection-b");
        Assert.Equal("node-b", router.Route("llama3", "conv-1")!.NodeId);
        Assert.Equal("node-b", router.Route("llama3", "conv-1")!.NodeId);
    }

    [Fact]
    public void LeastBusyDefaultIgnoresThroughputAndRoundRobins()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        var (router, tracker) = NewRouter(registry, RouterOptions.StrategyLeastBusy);

        // Even with node-a measured much faster, the default strategy must behave exactly as v2.7:
        // equally-loaded nodes round-robin, throughput is ignored.
        tracker.RecordFromResponse("node-a", Resp("llama3", evalCount: 100000, durationNs: 100_000_000));

        Assert.Equal("node-a", router.Route("llama3")!.NodeId);
        Assert.Equal("node-b", router.Route("llama3")!.NodeId);
        Assert.Equal("node-a", router.Route("llama3")!.NodeId);
    }

    [Fact]
    public void ThroughputWithNoMeasurementsFallsBackToRoundRobin()
    {
        var registry = new NodeRegistry();
        SeedTwoNodes(registry);
        var (router, _) = NewRouter(registry, RouterOptions.StrategyThroughput);

        var picks = new HashSet<string>();
        for (var i = 0; i < 6; i++) picks.Add(router.Route("llama3")!.NodeId);

        Assert.Equal(2, picks.Count); // nothing measured → both still used
    }

    [Fact]
    public void TrackerFoldsSamplesAndReportsNullForUnmeasured()
    {
        var tracker = new ThroughputTracker();
        Assert.Null(tracker.GetTokensPerSecond("node-a", "llama3"));

        tracker.RecordFromResponse("node-a", Resp("llama3", evalCount: 100, durationNs: 100_000_000)); // 1000 tok/s
        var measured = tracker.GetTokensPerSecond("node-a", "llama3");

        Assert.NotNull(measured);
        Assert.True(measured > 0);
        Assert.Null(tracker.GetTokensPerSecond("node-a", "other-model"));
        // A load response (no tokens, no duration) records nothing.
        tracker.RecordFromResponse("node-b", "{\"model\":\"llama3\",\"eval_count\":0,\"eval_duration\":0}");
        Assert.Null(tracker.GetTokensPerSecond("node-b", "llama3"));
    }

    private static string Resp(string model, long evalCount, long durationNs) =>
        $"{{\"model\":\"{model}\",\"eval_count\":{evalCount},\"eval_duration\":{durationNs}}}";

    private static (Router, ThroughputTracker) NewRouter(NodeRegistry registry, string strategy)
    {
        var (router, tracker, _) = NewRouterWithAffinity(registry, strategy);
        return (router, tracker);
    }

    private static (Router, ThroughputTracker, ConversationAffinity) NewRouterWithAffinity(NodeRegistry registry, string strategy)
    {
        var options = Options.Create(new RouterOptions
        {
            AffinitySlidingMinutes = 10,
            AffinityLoadBreakThreshold = 2,
            Strategy = strategy
        });
        var affinity = new ConversationAffinity(options);
        var tracker = new ThroughputTracker();
        return (new Router(registry, affinity, tracker, options), tracker, affinity);
    }

    private static void SeedTwoNodes(NodeRegistry registry)
    {
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-a", Registration("node-a", "alpha-node"), now);
        registry.Upsert("connection-b", Registration("node-b", "beta-node"), now);
        registry.ReportModels("connection-a", new NodeModels("node-a", [new ModelInfo("llama3", "da", 1)], now), now);
        registry.ReportModels("connection-b", new NodeModels("node-b", [new ModelInfo("llama3", "db", 1)], now), now);
    }

    private static NodeRegistration Registration(string nodeId, string name) =>
        new(nodeId, name, "http://localhost:11434/", "2.8.0");
}
