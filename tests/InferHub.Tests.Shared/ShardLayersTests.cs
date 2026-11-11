using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 137 D4: how llama.cpp divides layers over devices for a tensor split with every layer offloaded — the cumulative,
/// normalized split, layer <c>il</c> of <c>n</c> (the output layer is <c>il = n</c>) on the first device whose share exceeds
/// <c>il / (n + 1)</c>.
/// </summary>
public sealed class ShardLayersTests
{
    [Fact]
    public void ThreeEqualWorkersTakeElevenElevenAndTenLayersAndTheLastTakesTheOutput()
    {
        // Llama 3.1 8B: 32 blocks. Shares 1/3, 2/3, 1 against il/33.
        var split = ShardLayers.Split(32, [3072, 3072, 3072]);

        Assert.Equal([(0, 11, false), (11, 11, false), (22, 10, true)], split);
        Assert.Equal(32, split.Sum(s => s.Count));
    }

    [Fact]
    public void LayersFollowTheWeights()
    {
        // Shares 0.25, 0.5, 1: il/33 < 0.25 → 0–8, < 0.5 → 9–16, the rest and the output on the third.
        Assert.Equal([(0, 9, false), (9, 8, false), (17, 15, true)], ShardLayers.Split(32, [3072, 3072, 6144]));
    }

    [Fact]
    public void ATinyShareCanHoldNoLayerAtAllAndNothingIsSplitWithoutLayersOrWorkers()
    {
        var split = ShardLayers.Split(4, [1000, 1, 1000]);

        Assert.Equal(0, split[1].Count);
        Assert.Equal(4, split.Sum(s => s.Count));
        Assert.Empty(ShardLayers.Split(0, [1, 1]));
        Assert.Empty(ShardLayers.Split(32, []));
    }
}
