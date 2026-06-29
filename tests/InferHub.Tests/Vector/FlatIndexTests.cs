using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Tests.Vector;

public class FlatIndexTests
{
    [Fact]
    public void CosineRanksAlignedVectorsHighest()
    {
        var index = new FlatIndex(3, DistanceMetric.Cosine);
        index.Upsert(Record("close", [1f, 0.1f, 0f]));
        index.Upsert(Record("far", [0f, 1f, 0f]));
        index.Upsert(Record("opposite", [-1f, -0.1f, 0f]));

        var matches = index.Query([1f, 0f, 0f], k: 3, filter: null);

        Assert.Equal(new[] { "close", "far", "opposite" }, matches.Select(m => m.Id).ToArray());
        Assert.True(matches[0].Score > matches[1].Score);
        Assert.True(matches[1].Score > matches[2].Score);
    }

    [Fact]
    public void DotProductFavoursLargerMagnitudeWhenAligned()
    {
        var index = new FlatIndex(2, DistanceMetric.Dot);
        index.Upsert(Record("big", [10f, 0f]));
        index.Upsert(Record("small", [1f, 0f]));

        var matches = index.Query([1f, 0f], k: 2, filter: null);

        Assert.Equal("big", matches[0].Id);
        Assert.True(matches[0].Score > matches[1].Score);
    }

    [Fact]
    public void L2RanksClosestFirst()
    {
        var index = new FlatIndex(2, DistanceMetric.L2);
        index.Upsert(Record("close", [0.1f, 0f]));
        index.Upsert(Record("mid", [1f, 0f]));
        index.Upsert(Record("far", [5f, 0f]));

        var matches = index.Query([0f, 0f], k: 3, filter: null);

        Assert.Equal(new[] { "close", "mid", "far" }, matches.Select(m => m.Id).ToArray());
        Assert.True(matches[0].Score < matches[1].Score);
        Assert.True(matches[1].Score < matches[2].Score);
    }

    [Fact]
    public void TopKLimitsResults()
    {
        var index = new FlatIndex(2, DistanceMetric.Dot);
        for (var i = 0; i < 10; i++)
        {
            index.Upsert(Record($"id-{i}", [i, 0]));
        }

        var matches = index.Query([1f, 0f], k: 3, filter: null);

        Assert.Equal(3, matches.Count);
        Assert.Equal("id-9", matches[0].Id);
    }

    [Fact]
    public void DeleteRemovesFromIndex()
    {
        var index = new FlatIndex(2, DistanceMetric.Dot);
        index.Upsert(Record("a", [1f, 0f]));
        index.Upsert(Record("b", [2f, 0f]));

        Assert.True(index.Delete("b"));
        Assert.Equal(1, index.Count);

        var matches = index.Query([1f, 0f], k: 5, filter: null);
        Assert.Single(matches);
        Assert.Equal("a", matches[0].Id);
    }

    [Fact]
    public void UpsertRejectsWrongDimension()
    {
        var index = new FlatIndex(3, DistanceMetric.Cosine);

        Assert.Throws<ArgumentException>(() => { index.Upsert(Record("bad", [1f, 0f])); });
    }

    [Fact]
    public void QueryRejectsWrongDimension()
    {
        var index = new FlatIndex(3, DistanceMetric.Cosine);
        index.Upsert(Record("a", [1f, 0f, 0f]));

        Assert.Throws<ArgumentException>(() => { index.Query([1f, 0f], k: 1, filter: null); });
    }

    [Fact]
    public void FilterMatchesOnlyRecordsWithAllMetadataPairs()
    {
        var index = new FlatIndex(2, DistanceMetric.Dot);
        index.Upsert(Record("a", [1f, 0f], metadata: new Dictionary<string, string> { ["tag"] = "alpha" }));
        index.Upsert(Record("b", [1f, 0f], metadata: new Dictionary<string, string> { ["tag"] = "beta" }));

        var matches = index.Query([1f, 0f], k: 5, filter: new Dictionary<string, string> { ["tag"] = "alpha" });

        Assert.Single(matches);
        Assert.Equal("a", matches[0].Id);
    }

    [Fact]
    public void EnumerateLiveReturnsOriginalVectors()
    {
        var index = new FlatIndex(2, DistanceMetric.Cosine);
        index.Upsert(Record("a", [3f, 4f]));

        var record = Assert.Single(index.EnumerateLive());
        Assert.Equal(3f, record.Vector[0]);
        Assert.Equal(4f, record.Vector[1]);
    }

    private static VectorRecord Record(string id, float[] vector, IReadOnlyDictionary<string, string>? metadata = null) =>
        new(id, vector, Payload: null, Metadata: metadata, SeqNo: 0, TimestampUtc: DateTimeOffset.UtcNow);
}
