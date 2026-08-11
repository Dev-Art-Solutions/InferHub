using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;

namespace InferHub.Tests.Vector;

public class HybridSearchTests
{
    private static VectorMatch M(string id) => new(id, 0.0, null, null);

    [Fact]
    public void FuseRewardsAgreementAcrossBranches()
    {
        // "b" is rank 2 in vector but rank 1 in keyword; the two together should beat "a", which is
        // rank 1 in vector but absent from keyword.
        var vector = new[] { M("a"), M("b"), M("c") };
        var keyword = new[] { M("b"), M("d") };

        var fused = HybridSearch.Fuse(vector, keyword, k: 4);

        Assert.Equal("b", fused[0].Id);
    }

    [Fact]
    public void FuseUsesRrfConstantAndSetsFusedScore()
    {
        var vector = new[] { M("a") };   // rank 1 -> 1/(60+1)
        var keyword = new[] { M("a") };  // rank 1 -> 1/(60+1)

        var fused = HybridSearch.Fuse(vector, keyword, k: 1);

        var expected = 2.0 / (HybridSearch.RrfK + 1);
        Assert.Equal("a", fused[0].Id);
        Assert.Equal(expected, fused[0].Score, precision: 10);
    }

    [Fact]
    public void FuseTakesTopK()
    {
        var vector = new[] { M("a"), M("b"), M("c") };
        var keyword = new[] { M("c"), M("b"), M("a") };

        var fused = HybridSearch.Fuse(vector, keyword, k: 2);

        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void FuseWithOneEmptyBranchDegradesToTheOther()
    {
        var vector = new[] { M("a"), M("b") };

        var fused = HybridSearch.Fuse(vector, Array.Empty<VectorMatch>(), k: 5);

        Assert.Equal(["a", "b"], fused.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void FuseDeduplicatesRecordsSeenInBothBranches()
    {
        var vector = new[] { M("a"), M("b") };
        var keyword = new[] { M("a"), M("b") };

        var fused = HybridSearch.Fuse(vector, keyword, k: 10);

        Assert.Equal(2, fused.Count);
    }

    [Theory]
    [InlineData("vector", true)]
    [InlineData("keyword", true)]
    [InlineData("hybrid", true)]
    [InlineData("HYBRID", true)]
    [InlineData("dense", false)]
    [InlineData("", false)]
    public void RetrievalModesParseKnownValuesOnly(string value, bool expected)
    {
        Assert.Equal(expected, RetrievalModes.TryParse(value, out _));
    }
}
