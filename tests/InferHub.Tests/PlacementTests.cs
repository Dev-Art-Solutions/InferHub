using InferHub.Coordinator.Services;

namespace InferHub.Tests;

public class PlacementTests
{
    [Fact]
    public void PullsOntoEmptyNodesToReachReplicas()
    {
        var nodes = new[] { Node("a"), Node("b"), Node("c") };

        var plan = ModelPlacement.Choose(nodes, Holders(), replicas: 2);

        Assert.Equal(2, plan.PullConnectionIds.Count);
        Assert.True(plan.Satisfied);
        Assert.Equal(0, plan.AlreadyPresent);
    }

    [Fact]
    public void SkipsCordonedNodes()
    {
        var nodes = new[] { Node("a"), Node("b", cordoned: true) };

        var plan = ModelPlacement.Choose(nodes, Holders(), replicas: 2);

        Assert.Single(plan.PullConnectionIds);
        Assert.Equal("a", plan.PullConnectionIds[0]);
        Assert.False(plan.Satisfied);
        Assert.Equal(1, plan.Shortfall);
    }

    [Fact]
    public void SkipsNodesThatAlreadyHaveIt()
    {
        var nodes = new[] { Node("a"), Node("b") };

        // 'a' already holds the model; only 'b' should be pulled onto.
        var plan = ModelPlacement.Choose(nodes, Holders("a"), replicas: 2);

        Assert.Equal(1, plan.AlreadyPresent);
        Assert.Single(plan.PullConnectionIds);
        Assert.Equal("b", plan.PullConnectionIds[0]);
        Assert.True(plan.Satisfied);
    }

    [Fact]
    public void SkipsNonManageableNodesAsCandidates()
    {
        var nodes = new[] { Node("a"), Node("b", manageable: false) };

        var plan = ModelPlacement.Choose(nodes, Holders(), replicas: 2);

        // 'b' cannot be pulled onto, so only 'a' is a candidate: one pull, not satisfied.
        Assert.Single(plan.PullConnectionIds);
        Assert.Equal("a", plan.PullConnectionIds[0]);
        Assert.Equal(1, plan.EligibleCandidates);
        Assert.False(plan.Satisfied);
    }

    [Fact]
    public void NonManageableHolderCountsTowardTarget()
    {
        // A vLLM node (holder, cannot manage) plus an idle manageable node. replicas=1 is already
        // satisfied by the vLLM holder — nothing should be pulled.
        var nodes = new[] { Node("vllm", manageable: false), Node("ollama") };

        var plan = ModelPlacement.Choose(nodes, Holders("vllm"), replicas: 1);

        Assert.Equal(1, plan.NonManageableHolders);
        Assert.Equal(0, plan.EffectiveTarget);
        Assert.Empty(plan.PullConnectionIds);
        Assert.True(plan.Satisfied);
    }

    [Fact]
    public void StopsWhenNotEnoughCandidatesAndSaysSo()
    {
        var nodes = new[] { Node("a") };

        var plan = ModelPlacement.Choose(nodes, Holders(), replicas: 3);

        Assert.Single(plan.PullConnectionIds);
        Assert.False(plan.Satisfied);
        Assert.Equal(2, plan.Shortfall);
    }

    private static IReadOnlySet<string> Holders(params string[] connectionIds) =>
        connectionIds.ToHashSet(StringComparer.Ordinal);

    private static NodeSnapshot Node(string id, bool cordoned = false, bool manageable = true, int load = 0) =>
        new(
            ConnectionId: id,
            NodeId: id,
            Name: id,
            OllamaEndpoint: "http://x",
            Version: "1",
            LastSeenUtc: DateTimeOffset.UtcNow,
            AgeSeconds: 0,
            InFlight: 0,
            LocalInFlight: load,
            ModelCount: 0,
            Labels: new Dictionary<string, string>(),
            MaxConcurrency: null,
            Cordoned: cordoned,
            SupportsModelManagement: manageable);
}
