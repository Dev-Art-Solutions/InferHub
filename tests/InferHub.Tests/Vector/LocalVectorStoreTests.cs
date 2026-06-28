using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class LocalVectorStoreTests : IDisposable
{
    private readonly string _root;

    public LocalVectorStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-lvs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateUpsertQueryRoundTrip()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 3, distance: "cosine");

        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("b", [0f, 1f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("c", [0.9f, 0.1f, 0f]));

        var matches = await store.QueryAsync("docs", new VectorQuery([1f, 0f, 0f], K: 2));

        Assert.Equal(2, matches.Count);
        Assert.Equal("a", matches[0].Id);
        Assert.Equal("c", matches[1].Id);
    }

    [Fact]
    public async Task RestartRebuildsIndexFromRawStore()
    {
        var first = NewStore();
        await first.CreateCollectionAsync("docs", dimension: 2, distance: "dot");
        await first.UpsertAsync("docs", new VectorUpsert("a", [3f, 0f]));
        await first.UpsertAsync("docs", new VectorUpsert("b", [1f, 0f]));
        await first.UpsertAsync("docs", new VectorUpsert("c", [5f, 0f]));
        first.Dispose();

        using var second = NewStore();
        var info = await second.GetCollectionAsync("docs");
        Assert.NotNull(info);
        Assert.Equal(3, info!.RecordCount);

        var matches = await second.QueryAsync("docs", new VectorQuery([1f, 0f], K: 3));
        Assert.Equal(new[] { "c", "a", "b" }, matches.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task SnapshotKickedAfterThresholdAndRestartStillCorrect()
    {
        var first = NewStore(snapshotEveryOps: 5);
        await first.CreateCollectionAsync("docs", dimension: 2, distance: "dot");
        for (var i = 0; i < 12; i++)
        {
            await first.UpsertAsync("docs", new VectorUpsert($"id-{i}", [i, 0]));
        }
        first.Dispose();

        var collectionDir = Path.Combine(_root, "docs");
        Assert.True(File.Exists(Path.Combine(collectionDir, "snapshot.jsonl")));

        using var second = NewStore(snapshotEveryOps: 5);
        var matches = await second.QueryAsync("docs", new VectorQuery([1f, 0f], K: 3));
        Assert.Equal(new[] { "id-11", "id-10", "id-9" }, matches.Select(m => m.Id).ToArray());

        var info = await second.GetCollectionAsync("docs");
        Assert.Equal(12, info!.RecordCount);
    }

    [Fact]
    public async Task DimensionMismatchOnUpsertThrows()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 3, distance: "cosine");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f])));
    }

    [Fact]
    public async Task DimensionMismatchOnQueryThrows()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 3, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f, 0f]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.QueryAsync("docs", new VectorQuery([1f, 0f], K: 1)));
    }

    [Fact]
    public async Task UpsertingMissingCollectionThrowsKeyNotFound()
    {
        using var store = NewStore();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpsertAsync("missing", new VectorUpsert("a", [1f, 0f])));
    }

    [Fact]
    public async Task DropRemovesCollection()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        var dropped = await store.DropCollectionAsync("docs");
        Assert.True(dropped);

        Assert.False(Directory.Exists(Path.Combine(_root, "docs")));
        Assert.Null(await store.GetCollectionAsync("docs"));
    }

    [Fact]
    public async Task DeleteReturnsFalseWhenIdMissing()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        var removed = await store.DeleteAsync("docs", "ghost");

        Assert.False(removed);
    }

    [Fact]
    public async Task CreateDuplicateCollectionThrows()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine"));
    }

    [Fact]
    public async Task L2DistanceReturnsClosestFirst()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "l2");
        await store.UpsertAsync("docs", new VectorUpsert("close", [0.1f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("mid", [1f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("far", [10f, 0f]));

        var matches = await store.QueryAsync("docs", new VectorQuery([0f, 0f], K: 3));

        Assert.Equal(new[] { "close", "mid", "far" }, matches.Select(m => m.Id).ToArray());
    }

    private LocalVectorStore NewStore(int snapshotEveryOps = 5000)
    {
        var opts = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = _root,
            Distance = "cosine",
            SnapshotEveryOps = snapshotEveryOps
        });
        return new LocalVectorStore(opts, NullLogger<LocalVectorStore>.Instance);
    }
}
