using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 134: what a node offers groups — read from what llama.cpp's worker prints (D2), reserved from the node's own
/// estimate (D4), all a group counts on that node (D4), and one RPC protocol per group (D3).
/// </summary>
public sealed class ShardOfferTests
{
    // 133's 70B: 80 layers, ~40 GiB at Q4_K_M, ~25 GiB at Q2_K.
    private static ModelFacts Seventy() => new(
        ModelFacts.SourceGguf, "Llama-70B", "llama", "text-generation", 70_000_000_000, 80, 80, 8 * 256, 131072, null, null, 0,
        [new ModelVariant("Q4_K_M", 42_520_000_000), new ModelVariant("Q2_K", 26_400_000_000)], []);

    private static GroupCandidate Cpu(string id, long ramMiB, ShardOffer? offer = null) =>
        new(id, id.ToUpperInvariant(), new MachineFacts([], ramMiB, ramMiB, 8,
            [new EngineFacts(FitEngines.LlamaCpp, "llama", true, null, "/models", 1 << 21, null, null)], [], offer));

    private static ShardOffer Ram(long gib, string protocol = "8.0.1") => new(gib * 1024, 0, protocol);

    [Theory]
    [InlineData("Starting RPC server v8.0.1", "8.0.1")] // b12182, the lab
    [InlineData("Starting RPC server v5.0.0", "5.0.0")] // b10424, winget
    [InlineData("  endpoint       : 127.0.0.1:50052", null)]
    [InlineData("load_backend: loaded RPC backend from C:\\llama\\ggml-rpc.dll", null)]
    public void TheProtocolIsWhatTheWorkerAnnounces(string line, string? protocol) =>
        Assert.Equal(protocol, ShardBanner.Protocol(line));

    [Fact]
    public void ADeviceLineIsReadWithItsMemoryAndTheOtherBannerLinesAreNot()
    {
        var device = ShardBanner.Device("  CPU: AMD Ryzen Threadripper PRO 5975WX 32-Cores      (261985 MiB, 110652 MiB free)");

        Assert.Equal("CPU: AMD Ryzen Threadripper PRO 5975WX 32-Cores      (261985 MiB, 110652 MiB free)", device);
        Assert.Equal(261985, ShardBanner.DeviceMiB(device!));
        Assert.Equal("Vulkan0: NVIDIA GeForce RTX 3090 Ti (24575 MiB, 23000 MiB free)", ShardBanner.Device("  Vulkan0: NVIDIA GeForce RTX 3090 Ti (24575 MiB, 23000 MiB free)"));
        Assert.Null(ShardBanner.Device("  transport      : TCP"));
        Assert.Null(ShardBanner.Device("  local cache    : n/a"));
        Assert.Null(ShardBanner.Device("Devices:"));
    }

    [Fact]
    public void WhatIsOfferedToGroupsIsNotCountedForTheNodesOwnModels()
    {
        var model = Seventy() with { Variants = [Seventy().Variants[1]] };
        var mine = Cpu("a", 40 * 1024).Machine;
        var offering = mine with { Shards = Ram(16) };

        var before = FitEstimator.Estimate(model, mine, 8192).Single(o => o.Engine == FitEngines.LlamaCpp);
        var after = FitEstimator.Estimate(model, offering, 8192).Single(o => o.Engine == FitEngines.LlamaCpp);

        Assert.True(before.Fits, string.Join(" | ", before.Why));
        Assert.False(after.Fits, string.Join(" | ", after.Why));
        Assert.Contains(after.Why, w => w.Contains("16 GiB of RAM is offered to groups (Node:Shards)"));
        Assert.DoesNotContain(before.Why, w => w.Contains("offered to groups"));
    }

    [Fact]
    public void AMemberThatOfferedIsCountedByItsOfferNeverMoreThanItHas()
    {
        Assert.Equal((24 * 1024L, "offer:ram"), GroupFit.Capacity(Cpu("a", 48 * 1024, Ram(24)).Machine));

        // An offer above what the machine could give whole is capped at the whole.
        var (mib, device) = GroupFit.Capacity(Cpu("b", 16 * 1024, Ram(64)).Machine);
        Assert.Equal(GroupFit.Capacity(Cpu("b", 16 * 1024).Machine).MiB, mib);
        Assert.Equal("offer:ram", device);

        Assert.Equal("offer:card+ram", GroupFit.Capacity(Cpu("c", 48 * 1024, new ShardOffer(8192, 4096, "8.0.1")).Machine).Device);
    }

    [Fact]
    public void TwoNodesThatOfferedHoldWhatNeitherHoldsAloneAndThePlanSaysTheyWereCountedByTheirOffer()
    {
        var plan = GroupFit.Plan(Seventy(), Seventy().Variants[0], [Cpu("a", 48 * 1024, Ram(24)), Cpu("b", 48 * 1024, Ram(24))], 8192);

        Assert.True(plan.Fits, string.Join(" | ", plan.Why));
        Assert.Null(plan.Alone);
        Assert.Equal(2, plan.Members.Count);
        Assert.All(plan.Members, m => Assert.Equal("offer:ram", m.Device));
        Assert.All(plan.Members, m => Assert.True(m.MemoryMiB <= 24 * 1024, $"{m.Name} takes {m.MemoryMiB} of a 24 GiB offer"));
        Assert.Contains(plan.Why, w => w.Contains("2 members counted by what their owners offered groups"));
        Assert.DoesNotContain(plan.Why, w => w.Contains("offer nothing to groups"));
    }

    [Fact]
    public void ANodeWithACardThatOfferedRamOnlyIsNotCalledANodeWithNoCard()
    {
        // Found live: shard-a has a 3090 Ti and offered 24 GiB of RAM; the plan said "has no card".
        var carded = new GroupCandidate("a", "A", new MachineFacts([new GpuFacts(0, "card", 24576, 24000)], 48 * 1024, 48 * 1024, 8,
            [new EngineFacts(FitEngines.LlamaCpp, "llama", true, null, "/models", 1 << 21, null, null)], [], Ram(24)));
        var plan = GroupFit.Plan(Seventy(), Seventy().Variants[0], [carded, Cpu("b", 48 * 1024, Ram(24))], 8192);

        Assert.True(plan.Fits, string.Join(" | ", plan.Why));
        Assert.Contains(plan.Why, w => w.StartsWith("A offered groups RAM only", StringComparison.Ordinal));
        Assert.Contains(plan.Why, w => w.StartsWith("B has no card", StringComparison.Ordinal));
    }

    [Fact]
    public void ANodeOnAnotherRpcProtocolIsLeftOutAndNamed()
    {
        var plan = GroupFit.Plan(Seventy(), Seventy().Variants[0],
            [Cpu("a", 48 * 1024, Ram(24)), Cpu("b", 48 * 1024, Ram(24)), Cpu("c", 48 * 1024, Ram(32, "5.0.0"))], 8192);

        Assert.True(plan.Fits, string.Join(" | ", plan.Why));
        Assert.DoesNotContain(plan.Members, m => m.NodeId == "c");
        Assert.Contains(plan.Why, w => w.Contains("another llama.cpp RPC protocol than the group's v8.0.1") && w.Contains("C (v5.0.0)"));
    }

    [Fact]
    public void ANodeThatOfferedNothingIsStillCountedWholeAndThePlanSaysSo()
    {
        var plan = GroupFit.Plan(Seventy(), Seventy().Variants[0], [Cpu("a", 48 * 1024, Ram(24)), Cpu("w", 30 * 1024)], 8192);

        Assert.True(plan.Fits, string.Join(" | ", plan.Why));
        Assert.Equal(["ram", "offer:ram"], plan.Members.Select(m => m.Device).Order().Reverse().ToArray());
        Assert.Contains(plan.Why, w => w.Contains("W offers nothing to groups yet (Node:Shards)"));
    }
}
