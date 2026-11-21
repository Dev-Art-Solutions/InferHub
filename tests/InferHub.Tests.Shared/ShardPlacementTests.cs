using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 144 D1: the hub places a group by itself — a head that holds the file and may head, and the peers whose offers hold
/// the model with the smallest sum of the head's round trips to them; then fewest members, then members holding the file.
/// </summary>
public sealed class ShardPlacementTests
{
    private const string Model = "llama31-8b.gguf";

    private static ShardPeerState Peer(string id, double? rtt, string? error = null) =>
        new(id, "shard-" + id, $"shard-{id}:50053", "127.0.0.1:1", rtt, error, DateTimeOffset.UtcNow);

    private static PlacementCandidate Node(string id, long offerMiB, IReadOnlyList<ShardPeerState>? peers = null, long? fileMiB = null, bool mayHead = false, string protocol = "8.0.1") =>
        new(id, "shard-" + id, offerMiB, fileMiB is { } mib ? new ShardModelFile(Model, mib, 32, null) : null, mayHead, protocol, peers ?? []);

    [Fact]
    public void TheNearestPeerThatMakesTheModelFitIsChosenNotTheLargest()
    {
        var result = ShardPlacement.Place(Model, 0,
        [
            Node("a", 4096, [Peer("b", 0.4), Peer("c", 0.3), Peer("d", 15)], fileMiB: 6000, mayHead: true),
            Node("b", 4096),
            Node("c", 4096),
            Node("d", 8192)
        ]);

        Assert.True(result.Placed, result.Why);
        Assert.Equal("a", result.HeadId);
        Assert.Equal(["c"], result.MemberIds);
        Assert.Contains("left out: shard-d: 15 ms from shard-a, not needed", result.Notes);
    }

    [Fact]
    public void TwoNearMembersBeatOneFarOneWhenTheirRoundTripsSumToLess()
    {
        var result = ShardPlacement.Place(Model, 0,
        [
            Node("a", 4096, [Peer("b", 0.4), Peer("c", 0.3), Peer("d", 15)], fileMiB: 9000, mayHead: true),
            Node("b", 4096),
            Node("c", 4096),
            Node("d", 8192)
        ]);

        Assert.True(result.Placed, result.Why);
        Assert.Equal(["c", "b"], result.MemberIds);
        Assert.Equal(0.7, result.RoundTripMs);
        Assert.StartsWith("members shard-c (0.30 ms, offers 4 GiB), shard-b (0.40 ms, offers 4 GiB): 0.70 ms of round trips per pass", result.Notes[1]);
    }

    [Fact]
    public void AJitterBelowATenthOfAMillisecondPrefersFewerMembersThenOnesThatHoldTheFile()
    {
        // b alone, or c alone: both hold the model; c holds the file too, so it loads from its own disk (143).
        var result = ShardPlacement.Place(Model, 0,
        [
            Node("a", 4096, [Peer("b", 0.31), Peer("c", 0.34)], fileMiB: 6000, mayHead: true),
            Node("b", 4096),
            Node("c", 4096, fileMiB: 6000)
        ]);

        Assert.Equal(["c"], result.MemberIds);
        Assert.Contains("holds the file", result.Notes[1]);
    }

    [Fact]
    public void NoPlacementIsMadeOnAnUnmeasuredRowOfTheMatrix()
    {
        var result = ShardPlacement.Place(Model, 0,
        [
            Node("a", 4096, [Peer("b", 0.4), Peer("c", null)], fileMiB: 6000, mayHead: true),
            Node("b", 4096),
            Node("c", 4096)
        ]);

        Assert.False(result.Placed);
        Assert.Equal("waiting for the latency matrix: shard-a has not measured its round trip to shard-c yet", result.Why);
    }

    [Fact]
    public void AnUnreachablePeerAndOneOnAnotherProtocolAreLeftOutAndSaidSo()
    {
        var result = ShardPlacement.Place(Model, 0,
        [
            Node("a", 4096, [Peer("b", 0.4), Peer("c", null, "connection refused"), Peer("d", 0.2)], fileMiB: 6000, mayHead: true),
            Node("b", 4096),
            Node("c", 4096),
            Node("d", 4096, protocol: "3.0.0")
        ]);

        Assert.Equal(["b"], result.MemberIds);
        Assert.Contains("left out: shard-c: shard-a cannot reach it (connection refused)", result.Notes);
        Assert.Contains("left out: shard-d: llama.cpp RPC v3.0.0, shard-a v8.0.1", result.Notes);
    }

    [Fact]
    public void TheHeadIsANodeThatHoldsTheFileAndWhoseOwnerLetsItHead()
    {
        var none = ShardPlacement.Place(Model, 0, [Node("a", 4096), Node("b", 4096)]);
        Assert.StartsWith($"no node that can take part now reports {Model}", none.Why);

        var refused = ShardPlacement.Place(Model, 0, [Node("a", 4096, fileMiB: 6000), Node("b", 4096)]);
        Assert.Equal($"shard-a holds {Model}, and its owner has not set Node:Shards:AllowHead", refused.Why);

        // Two may head: the one whose nearest group sums to the smaller round trip heads.
        var two = ShardPlacement.Place(Model, 0,
        [
            Node("a", 4096, [Peer("b", 5), Peer("c", 6)], fileMiB: 6000, mayHead: true),
            Node("b", 4096, [Peer("a", 5), Peer("c", 0.2)], fileMiB: 6000, mayHead: true),
            Node("c", 4096, [Peer("a", 6), Peer("b", 0.2)])
        ]);
        Assert.Equal("b", two.HeadId);
        Assert.Equal(["c"], two.MemberIds);
    }

    [Fact]
    public void AHeadThatHoldsTheModelAloneIsToldToServeItAloneAndAModelTooBigForEveryoneSaysHowMuch()
    {
        var alone = ShardPlacement.Place(Model, 0, [Node("a", 16384, [Peer("b", 0.2)], fileMiB: 6000, mayHead: true), Node("b", 4096)]);
        Assert.False(alone.Placed);
        Assert.Contains("holds it alone", alone.Why);

        var big = ShardPlacement.Place(Model, 4096, [Node("a", 4096, [Peer("b", 0.2)], fileMiB: 40000, mayHead: true), Node("b", 4096)]);
        Assert.Equal($"shard-a and every peer it can reach offer 8 GiB, and {Model} needs about 40 GiB at 4,096 tokens", big.Why);
    }

    [Theory]
    [InlineData(6000, 512, new long[] { 4096, 4096 }, true)]
    [InlineData(9000, 512, new long[] { 4096, 4096 }, false)]
    [InlineData(100, 512, new long[] { 400, 8192 }, false)] // a member smaller than the working space holds nothing
    [InlineData(7000, 512, new long[] { 2048, 8192 }, true)]
    public void AGroupHoldsTheModelWhenEveryWorkersProportionalShareAndItsReserveFitItsOffer(long model, long reserve, long[] offers, bool holds) =>
        Assert.Equal(holds, ShardPlacement.Holds(model, reserve, offers));
}
