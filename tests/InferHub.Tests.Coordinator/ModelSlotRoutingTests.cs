using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 108 D1/D3. The router prefers a node with a free slot for the requested model — fewer of the
/// hub's own jobs for it than the node declared — and a fleet that declares nothing routes exactly as
/// it did before.
/// </summary>
public class ModelSlotRoutingTests
{
    [Fact]
    public void ANodeBusyWithTheModelLosesToOneBusyWithAnotherModel()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "nomic-embed-text");
        var router = NewRouter(registry);

        // Both nodes read load 1. Without the tier the tie rotates and every other chat waits inside
        // node A's engine for the one slot it has.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("node-b", router.Route("llama3", capability: "chat")!.NodeId);
        }
    }

    [Fact]
    public void NoDeclaredSlotsIsTodaysPick()
    {
        var registry = Seed(slotsA: null, slotsB: null);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "nomic-embed-text");
        var router = NewRouter(registry);

        var picks = Enumerable.Range(0, 4).Select(_ => router.Route("llama3", capability: "chat")!.NodeId).ToArray();

        Assert.Equal(["node-a", "node-b", "node-a", "node-b"], picks);
    }

    [Fact]
    public void EveryNodeFullStillRoutesAndBalancesAsBefore()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var router = NewRouter(registry);

        // A preference, never a filter: the engines queue as they always did.
        var picks = Enumerable.Range(0, 2).Select(_ => router.Route("llama3", capability: "chat")!.NodeId).ToArray();

        Assert.Equal(["node-a", "node-b"], picks);
    }

    [Fact]
    public void ASecondSlotIsAFreeSlot()
    {
        var registry = Seed(slotsA: 2, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var router = NewRouter(registry);

        Assert.Equal("node-a", router.Route("llama3", capability: "chat")!.NodeId);
        Assert.Equal("node-a", router.Route("llama3", capability: "chat")!.NodeId);
    }

    [Fact]
    public void ANodeThatDeclaresNothingIsAlwaysFree()
    {
        var registry = Seed(slotsA: 1, slotsB: null);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        registry.IncrementInFlight("connection-b", "llama3");
        var router = NewRouter(registry);

        Assert.Equal("node-b", router.Route("llama3", capability: "chat")!.NodeId);
    }

    [Fact]
    public void AStickyConversationLeavesANodeWhoseSlotIsTaken()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        var router = NewRouter(registry);

        var first = router.Route("llama3", "conversation-1", capability: "chat")!;
        var other = first.NodeId == "node-a" ? "node-b" : "node-a";
        registry.IncrementInFlight(first.ConnectionId, "llama3");
        registry.IncrementInFlight(other.Replace("node-", "connection-"), "nomic-embed-text");

        Assert.Equal(other, router.Route("llama3", "conversation-1", capability: "chat")!.NodeId);
    }

    [Fact]
    public void ABareNameIsItsLatestTagAndCaseDoesNotMatter()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "Llama3:latest");

        Assert.Equal(1, registry.GetModelInFlight("connection-a", "llama3"));
        Assert.Equal(0, registry.GetModelInFlight("connection-a", "llama3:8b"));
    }

    [Fact]
    public void ReleasingAJobGivesBackBothCountersAndAGoneNodeForgetsItsModels()
    {
        var registry = Seed(slotsA: 1, slotsB: 1);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-a", null);

        Assert.Equal(2, registry.GetLocalInFlight("connection-a"));
        Assert.Equal(1, registry.GetModelInFlight("connection-a", "llama3"));

        registry.DecrementInFlight("connection-a", "llama3");
        Assert.Equal(1, registry.GetLocalInFlight("connection-a"));
        Assert.Equal(0, registry.GetModelInFlight("connection-a", "llama3"));

        registry.DecrementInFlight("connection-a", "llama3");
        Assert.Equal(0, registry.GetModelInFlight("connection-a", "llama3"));

        registry.IncrementInFlight("connection-a", "llama3");
        registry.Remove("connection-a");
        Assert.Equal(0, registry.GetModelInFlight("connection-a", "llama3"));
    }

    [Fact]
    public void TheSnapshotCarriesTheDeclarationAndOnlyBusyModels()
    {
        var registry = Seed(slotsA: 4, slotsB: null);
        registry.IncrementInFlight("connection-a", "llama3");
        registry.IncrementInFlight("connection-a", "nomic-embed-text");
        registry.DecrementInFlight("connection-a", "nomic-embed-text");

        var snapshots = registry.Snapshot(DateTimeOffset.UtcNow).ToDictionary(node => node.NodeId);

        Assert.Equal(4, snapshots["node-a"].ModelSlots);
        Assert.Equal(new Dictionary<string, int> { ["llama3"] = 1 }, snapshots["node-a"].ModelsInFlight);
        Assert.Null(snapshots["node-b"].ModelSlots);
    }

    [Fact]
    public void ADeclarationBelowOneIsNoDeclaration()
    {
        var registry = Seed(slotsA: 0, slotsB: -3);

        Assert.Null(registry.GetModelSlots("connection-a"));
        Assert.Null(registry.GetModelSlots("connection-b"));
    }

    [Theory]
    [InlineData("""{"model":"llama3","messages":[]}""", "llama3")]
    [InlineData("""{"options":{"model":"inner"},"stream":true,"model":"qwen2.5:0.5b"}""", "qwen2.5:0.5b")]
    [InlineData("""{"messages":[]}""", null)]
    [InlineData("""{"model":7}""", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    public void TheDispatcherReadsTheTopLevelModelOnly(string body, string? expected)
    {
        Assert.Equal(expected, Dispatcher.ModelOf(new InferenceJob(Guid.NewGuid(), "chat", body)));
    }

    private static NodeRegistry Seed(int? slotsA, int? slotsB)
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        foreach (var (id, slots) in new[] { ("a", slotsA), ("b", slotsB) })
        {
            registry.Upsert(
                $"connection-{id}",
                new NodeRegistration($"node-{id}", $"{id}-node", "http://localhost:11434/", "3.72.0", ModelSlots: slots),
                now);
            registry.ReportModels(
                $"connection-{id}",
                new NodeModels(
                    $"node-{id}",
                    [new ModelInfo("llama3", $"digest-{id}", 100), new ModelInfo("nomic-embed-text", $"embed-{id}", 50)],
                    now),
                now);
        }

        return registry;
    }

    private static Router NewRouter(NodeRegistry registry)
    {
        var options = Options.Create(new RouterOptions { AffinitySlidingMinutes = 10, AffinityLoadBreakThreshold = 2 });
        return new Router(registry, new ConversationAffinity(options), new ThroughputTracker(), options);
    }
}
