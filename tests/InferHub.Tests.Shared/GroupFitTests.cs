using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 133 (D2): a model split by whole layers across nodes — who holds it alone, how many it takes otherwise, the
/// layers in proportion and never past a member's capacity, the head being the one with the disk, and the refusals.
/// </summary>
public sealed class GroupFitTests
{
    // A 70B-shaped GGUF: 80 layers, 8 KV heads of 128+128 (320 KiB a token), a ~40 GiB Q4_K_M.
    private static ModelFacts Seventy(string source = ModelFacts.SourceGguf) => new(
        source, "Llama-70B", "llama", "text-generation", 70_000_000_000, 80, 80, 8 * 256, 131072, null, null, 0,
        [new ModelVariant("Q4_K_M", 42_520_000_000), new ModelVariant("Q8_0", 75_000_000_000), new ModelVariant("Q2_K", 26_400_000_000)], []);

    private static GroupCandidate Card(string id, long cardMiB, long? diskFreeMiB = 1 << 21, long ramMiB = 8192) =>
        new(id, id.ToUpperInvariant(), new MachineFacts([new GpuFacts(0, "card", cardMiB, cardMiB)], ramMiB, ramMiB, 16,
            [new EngineFacts(FitEngines.LlamaCpp, null, true, null, "/models", diskFreeMiB, null, null)], []));

    private static GroupCandidate CpuOnly(string id, long ramMiB) =>
        new(id, id.ToUpperInvariant(), new MachineFacts([], ramMiB, ramMiB, 8, [], []));

    private static GroupPlan Plan(params GroupCandidate[] nodes) =>
        GroupFit.Plan(Seventy(), Seventy().Variants[0], nodes, 8192);

    [Fact]
    public void AModelNoNodeHoldsFitsThreeTogetherInWholeLayersNoneOverItsCapacity()
    {
        var plan = Plan(Card("a", 16384), Card("b", 16384), Card("c", 16384), Card("d", 16384), CpuOnly("e", 8192));

        Assert.True(plan.Fits);
        Assert.Null(plan.Alone);
        Assert.Equal(3, plan.Members.Count);
        Assert.Equal(80, plan.Members.Sum(m => m.Layers));
        Assert.Equal(GroupFit.Head, plan.Members[0].Role);
        Assert.All(plan.Members.Skip(1), m => Assert.Equal(GroupFit.Worker, m.Role));
        Assert.All(plan.Members, m => Assert.True(m.MemoryMiB <= m.CapacityMiB, $"{m.Name}: {m.MemoryMiB} > {m.CapacityMiB}"));
        Assert.All(plan.Members, m => Assert.Equal("card+ram", m.Device));

        // Consecutive runs, head first, from layer 0 to 79.
        var next = 0;
        foreach (var member in plan.Members)
        {
            Assert.Equal(next, member.FirstLayer);
            next += member.Layers;
        }

        Assert.Contains(plan.Why, w => w.Contains("3 nodes hold its 80 layers"));
        Assert.Contains(plan.Why, w => w.Contains("round trip"));
    }

    [Fact]
    public void ANodeThatHoldsItAloneIsTheAnswerAndNoGroupIsFormed()
    {
        var plan = Plan(Card("small", 16384), Card("big", 49152));

        Assert.True(plan.Fits);
        Assert.Equal("big", plan.Alone);
        Assert.Single(plan.Members);
        Assert.Equal(80, plan.Members[0].Layers);
        Assert.Contains("no group is needed", plan.Why[0]);
    }

    [Fact]
    public void TooLittleEverywhereSaysHowManyLayersShortAndHowMuchMore()
    {
        var plan = Plan(Card("a", 16384), Card("b", 16384));

        Assert.False(plan.Fits);
        Assert.Empty(plan.Members);
        Assert.Contains(plan.Why, w => w.Contains("short") && w.Contains("more"));
    }

    [Fact]
    public void TheHeadIsTheMemberWithTheDiskForTheFileBecauseItSendsTheWeights()
    {
        var plan = Plan(Card("a", 16384, diskFreeMiB: 1024), Card("b", 16384), Card("c", 16384));

        Assert.True(plan.Fits);
        Assert.Equal("b", plan.Members.Single(m => m.Role == GroupFit.Head).NodeId);
        Assert.Equal(0, plan.Members[0].FirstLayer);
    }

    [Fact]
    public void NoMemberWithTheDiskIsNotAFitAndSaysWhy()
    {
        var plan = Plan(Card("a", 16384, 1024), Card("b", 16384, 1024), Card("c", 16384, 1024));

        Assert.False(plan.Fits);
        Assert.Contains(plan.Why, w => w.Contains("free disk"));
    }

    [Fact]
    public void ANodeWithTheMemoryButNotTheDiskIsNotAGroupOfOneItsOwnReasonIsTheAnswer()
    {
        // 64 GiB of RAM holds every layer, but the disk does not hold the file: 118 says no, and so does this.
        var plan = Plan(Card("a", 24576, diskFreeMiB: 1024, ramMiB: 65536));

        Assert.False(plan.Fits);
        Assert.Empty(plan.Members);
        Assert.Contains(plan.Why, w => w.Contains("A alone has the memory") && w.Contains("disk"));
    }

    [Fact]
    public void AMemberWithoutACardGivesItsRamAndIsNamedAsTheOneTheGroupWaitsFor()
    {
        var plan = Plan(Card("a", 24576), CpuOnly("cpu", 32768));

        Assert.True(plan.Fits);
        var cpu = plan.Members.Single(m => m.NodeId == "cpu");
        Assert.Equal("ram", cpu.Device);
        Assert.Contains(plan.Why, w => w.Contains("CPU has no card"));
    }

    [Fact]
    public void OnlyAGgufSplitsTheRestAreRefusedWithTheSentence()
    {
        Assert.Null(GroupFit.CanSplit(Seventy()));
        Assert.Contains("GGUF", GroupFit.CanSplit(Seventy(ModelFacts.SourceCheckpoint)));
        Assert.Contains("GGUF", GroupFit.CanSplit(Seventy(ModelFacts.SourceOllama)));
        Assert.Empty(GroupFit.PlanAll(Seventy(ModelFacts.SourceCheckpoint), [Card("a", 16384)], 8192));
    }

    [Fact]
    public void PlansAreOrderedAloneThenFewestMembersThenNowhere()
    {
        // 30 GiB: the Q2_K (25 GiB) fits one, the Q4 needs two, the Q8 (70 GiB) needs three — only two are here.
        var plans = GroupFit.PlanAll(Seventy(), [Card("a", 30720), Card("b", 30720)], 8192);

        Assert.Equal(["Q2_K", "Q4_K_M", "Q8_0"], plans.Select(p => p.Variant));
        Assert.NotNull(plans[0].Alone);
        Assert.Equal(2, plans[1].Members.Count);
        Assert.False(plans[2].Fits);
    }

    [Fact]
    public void LayersFollowWhatEachMemberHasAndTheRestGoesWhereThereIsRoom()
    {
        Assert.Equal([8, 2], GroupFit.Spread(10, [(300, 10), (100, 10)]));
        Assert.Equal([6, 4], GroupFit.Spread(10, [(300, 6), (100, 10)]));
    }
}
