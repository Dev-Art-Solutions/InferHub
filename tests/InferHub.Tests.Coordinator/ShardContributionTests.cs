using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;


namespace InferHub.Tests;

/// <summary>
/// Phase 148: which node gave how much to groups. Memory is a worker's offer while a group its head reports running lists it
/// (D3); tokens are the counts of each answer that head returned, credited to every worker it lists, with a share by layers (D1, D2).
/// </summary>
public sealed class ShardContributionTests
{
    private readonly NodeRegistry registry = new();
    private readonly NodeShardRegistry shards = new();
    private readonly ClusterLeadershipTests.FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    private ShardContribution Contribution() => new(shards, registry, time);

    private void Node(string id, long ramMiB, IReadOnlyList<ShardGroupState>? groups = null, string? organisation = null)
    {
        registry.Upsert("conn-" + id, new NodeRegistration(id, "shard-" + id, "http://x", "3.113.0") { Organisation = organisation }, time.GetUtcNow());
        shards.Report(new NodeShardState(id, NodeShardState.Running, ramMiB, 0, "127.0.0.1:50052", "8.0.1", [], null, time.GetUtcNow(), Groups: groups));
    }

    // The head (a) holds 16 of 32 layers, b 10, c 6 — the output layer on c, not counted (D2).
    private static ShardGroupState Group(string state = ShardGroupState.Running, bool community = false) =>
        new("llama-8b-split", "llama31-8b.gguf", state, ["shard-b", "shard-c"], "127.0.0.1:50100", null,
            [new ShardGroupLayers("shard-a", 0, 16, false), new ShardGroupLayers("shard-b", 16, 10, false, 1500, 1500), new ShardGroupLayers("shard-c", 26, 6, true, 900, 900)],
            community);

    private void Lab(string state = ShardGroupState.Running)
    {
        Node("a", 4096, [Group(state)]);
        Node("b", 2560);
        Node("c", 1536);
    }

    private const string Answer = """{"model":"llama-8b-split","message":{"role":"assistant","content":"…"},"done":true,"prompt_eval_count":20,"eval_count":44}""";

    [Fact]
    public void EveryWorkerOfARunningGroupIsCreditedItsOfferForTheTimeTheGroupRan()
    {
        Lab();
        var contribution = Contribution();
        contribution.Sample(time.GetUtcNow());

        for (var i = 0; i < 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            contribution.Sample(time.GetUtcNow());
        }

        var nodes = contribution.Report().Nodes.ToDictionary(n => n.Name);
        Assert.Equal(60, nodes["shard-b"].Seconds);
        Assert.Equal(Math.Round(2560 * 60 / 3600.0, 3), nodes["shard-b"].MiBHours);
        Assert.Equal(Math.Round(4096 * 60 / 3600.0, 3), nodes["shard-a"].MiBHours);
        Assert.Equal(["llama-8b-split"], nodes["shard-c"].GroupsNow);
        Assert.Equal(2560, nodes["shard-b"].OfferMiB);
        Assert.True(nodes["shard-b"].Connected);
    }

    [Fact]
    public void AGroupThatIsNotRunningCreditsNoMemory()
    {
        Lab(ShardGroupState.Starting);
        var contribution = Contribution();
        contribution.Sample(time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(10));
        contribution.Sample(time.GetUtcNow());

        Assert.All(contribution.Report().Nodes, n => Assert.Equal((0d, 0d), (n.Seconds, n.MiBHours)));
        Assert.All(contribution.Report().Nodes, n => Assert.Empty(n.GroupsNow));
    }

    [Fact]
    public void AGapLongerThanThreeSamplesIsCreditedThreeSamplesNotTheGap()
    {
        Lab();
        var contribution = Contribution();
        contribution.Sample(time.GetUtcNow());
        time.Advance(TimeSpan.FromMinutes(10));
        contribution.Sample(time.GetUtcNow());

        Assert.Equal(3 * ShardContribution.SampleSeconds, contribution.Report().Nodes.Single(n => n.Name == "shard-b").Seconds);
    }

    [Fact]
    public void AnAnswerFromTheHeadCreditsEveryWorkerItsTokensAndAShareByTheLayersItHeld()
    {
        Lab();
        var contribution = Contribution();

        contribution.RecordTokens("a", "llama-8b-split", Answer);
        contribution.RecordTokens("a", "llama-8b-split", Answer);

        var nodes = contribution.Report().Nodes.ToDictionary(n => n.Name);
        Assert.All(nodes.Values, n => Assert.Equal((2L, 40L, 88L), (n.Requests, n.PromptTokens, n.GeneratedTokens)));
        Assert.Equal(128 * 16 / 32.0, nodes["shard-a"].ShareTokens);
        Assert.Equal(128 * 10 / 32.0, nodes["shard-b"].ShareTokens);
        Assert.Equal(128 * 6 / 32.0, nodes["shard-c"].ShareTokens);
        Assert.Equal(["shard-a", "shard-b", "shard-c"], contribution.Report().Nodes.Select(n => n.Name));
        var group = Assert.Single(nodes["shard-b"].Groups);
        Assert.Equal(("llama-8b-split", "llama31-8b.gguf"), (group.Group, group.Model));
    }

    [Fact]
    public void AnAnswerForThatNameFromAnotherNodeOrForAnotherModelOrWithNoCountsCreditsNothing()
    {
        Lab();
        var contribution = Contribution();

        contribution.RecordTokens("b", "llama-8b-split", Answer);
        contribution.RecordTokens("a", "llama3.2:1b", Answer.Replace("llama-8b-split", "llama3.2:1b"));
        contribution.RecordTokens("a", "llama-8b-split", """{"model":"llama-8b-split","done":true}""");
        contribution.RecordTokens("a", "llama-8b-split", "not json");

        Assert.All(contribution.Report().Nodes, n => Assert.Equal(0, n.Requests));
    }

    [Fact]
    public void ANodeThatLeftKeepsWhatItGaveAndSaysItIsNotConnected()
    {
        Lab();
        var contribution = Contribution();
        contribution.RecordTokens("a", "llama-8b-split", Answer);

        registry.Remove("conn-c");
        shards.Forget("c");
        Node("a", 4096, [Group() with { Layers = [new ShardGroupLayers("shard-a", 0, 20, false), new ShardGroupLayers("shard-b", 20, 12, true)] }]);

        var c = contribution.Report().Nodes.Single(n => n.NodeId == "c");
        Assert.False(c.Connected);
        Assert.Null(c.OfferMiB);
        Assert.Equal("shard-c", c.Name);
        Assert.Equal(64 * 6 / 32.0, c.ShareTokens);
        Assert.Empty(c.GroupsNow);
    }

    [Fact]
    public void WithNoGroupsTheReportListsOnlyNodesThatOfferAndCreditsNothing()
    {
        Node("a", 4096);
        registry.Upsert("conn-z", new NodeRegistration("z", "plain", "http://x", "3.113.0"), time.GetUtcNow());
        var contribution = Contribution();
        contribution.Sample(time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(10));
        contribution.Sample(time.GetUtcNow());

        var only = Assert.Single(contribution.Report().Nodes);
        Assert.Equal(("shard-a", 0d, 0L), (only.Name, only.MiBHours, only.Requests));
    }
}
