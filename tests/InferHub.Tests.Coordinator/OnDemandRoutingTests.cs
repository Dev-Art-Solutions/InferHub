using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 86 D3. The router prefers a node whose card needs no switch — warm, then free, then cold —
/// and a fleet in which no node runs on demand routes exactly as it did in v3.50.
/// </summary>
public class OnDemandRoutingTests
{
    private static readonly OnDemandState HoldsChat = new("ollama", ["chat", "embed"], false, 0);
    private static readonly OnDemandState HoldsDiffusion = new("tool:diffusion", ["image", "video"], false, 0);
    private static readonly OnDemandState Free = new(null, [], false, 0);
    private static readonly OnDemandState Switching = new(null, [], true, 1);

    [Fact]
    public void NoOnDemandStateMeansOneTierAndTodaysPick()
    {
        var registry = Seed(a: null, b: null);
        var router = NewRouter(registry);

        var picks = Enumerable.Range(0, 4).Select(_ => router.Route("llama3", capability: "chat")!.NodeId).ToArray();

        Assert.Equal(["node-a", "node-b", "node-a", "node-b"], picks);
    }

    [Fact]
    public void AWarmNodeBeatsAColdOneEvenWhenBusier()
    {
        var registry = Seed(a: HoldsDiffusion, b: HoldsChat);
        registry.IncrementInFlight("connection-b");
        registry.IncrementInFlight("connection-b");
        registry.IncrementInFlight("connection-b");
        var router = NewRouter(registry);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("node-b", router.Route("llama3", capability: "chat")!.NodeId);
        }
    }

    [Fact]
    public void AFreeCardBeatsOneHeldBySomebodyElse()
    {
        var registry = Seed(a: HoldsDiffusion, b: Free);
        var router = NewRouter(registry);

        Assert.Equal("node-b", router.Route("llama3", capability: "chat")!.NodeId);
        Assert.Equal("node-b", router.Route("llama3", capability: "chat")!.NodeId);
    }

    [Fact]
    public void AWarmCardBeatsAFreeOne()
    {
        var registry = Seed(a: Free, b: HoldsChat);
        var router = NewRouter(registry);

        Assert.Equal("node-b", router.Route("llama3", capability: "chat")!.NodeId);
    }

    [Fact]
    public void ANodeThatDoesNotRunOnDemandIsAlwaysWarm()
    {
        var registry = Seed(a: null, b: HoldsDiffusion);
        var router = NewRouter(registry);

        Assert.Equal("node-a", router.Route("llama3", capability: "chat")!.NodeId);
        Assert.Equal("node-a", router.Route("llama3", capability: "chat")!.NodeId);
    }

    [Fact]
    public void AMidSwitchCardIsCold()
    {
        var registry = Seed(a: Free, b: Switching);
        var router = NewRouter(registry);

        Assert.Equal("node-a", router.Route("llama3", capability: "chat")!.NodeId);
    }

    [Fact]
    public void AColdNodeStillServesWhenItIsTheOnlyHolder()
    {
        var registry = Seed(a: HoldsDiffusion, b: Switching);
        var router = NewRouter(registry);

        // A preference, never a filter: both are cold, so today's least-busy picks among them.
        var picks = Enumerable.Range(0, 2).Select(_ => router.Route("llama3", capability: "chat")!.NodeId).ToArray();

        Assert.Equal(["node-a", "node-b"], picks);
    }

    [Fact]
    public void AStickyConversationLeavesANodeWhoseCardWentToAnotherService()
    {
        var registry = Seed(a: HoldsChat, b: HoldsChat);
        var router = NewRouter(registry);

        var first = router.Route("llama3", "conversation-1", capability: "chat")!;
        var other = first.NodeId == "node-a" ? "node-b" : "node-a";

        Beat(registry, first.NodeId, HoldsDiffusion);

        Assert.Equal(other, router.Route("llama3", "conversation-1", capability: "chat")!.NodeId);
    }

    [Fact]
    public void ARequestWithNoCapabilityIsNotTiered()
    {
        var registry = Seed(a: HoldsDiffusion, b: HoldsChat);
        var router = NewRouter(registry);

        var picks = Enumerable.Range(0, 2).Select(_ => router.Route("llama3")!.NodeId).ToArray();

        Assert.Equal(["node-a", "node-b"], picks);
    }

    [Fact]
    public void TheCardChangingHandsWakesTheConsoleAndAWaiterDoesNot()
    {
        var registry = Seed(a: HoldsChat, b: null);
        var changes = 0;
        registry.Changed += () => changes++;

        Beat(registry, "node-a", HoldsChat with { Waiting = 2 });
        Assert.Equal(0, changes);

        Beat(registry, "node-a", HoldsDiffusion);
        Assert.Equal(1, changes);

        Beat(registry, "node-a", null);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void TheSnapshotCarriesTheStateVerbatim()
    {
        var registry = Seed(a: HoldsDiffusion, b: null);

        var snapshots = registry.Snapshot(DateTimeOffset.UtcNow).ToDictionary(node => node.NodeId);

        Assert.Equal("tool:diffusion", snapshots["node-a"].OnDemand!.Holder);
        Assert.Null(snapshots["node-b"].OnDemand);
    }

    private static NodeRegistry Seed(OnDemandState? a, OnDemandState? b)
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, name) in new[] { ("a", "alpha-node"), ("b", "beta-node") })
        {
            registry.Upsert($"connection-{id}", new NodeRegistration($"node-{id}", name, "http://localhost:11434/", "3.51.0"), now);
            registry.ReportModels(
                $"connection-{id}",
                new NodeModels($"node-{id}", [new ModelInfo("llama3", $"digest-{id}", 100)], now),
                now);
        }

        Beat(registry, "node-a", a);
        Beat(registry, "node-b", b);
        return registry;
    }

    private static void Beat(NodeRegistry registry, string nodeId, OnDemandState? state)
    {
        var connection = "connection-" + nodeId["node-".Length..];
        registry.Touch(connection, new Heartbeat(nodeId, DateTimeOffset.UtcNow, 0, OnDemand: state), DateTimeOffset.UtcNow);
    }

    private static Router NewRouter(NodeRegistry registry)
    {
        var options = Options.Create(new RouterOptions { AffinitySlidingMinutes = 10, AffinityLoadBreakThreshold = 2 });
        return new Router(registry, new ConversationAffinity(options), new ThroughputTracker(), options);
    }
}
