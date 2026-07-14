using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

/// <summary>
/// The seam phase 23 added to <see cref="IVectorStore"/>: a metadata scan and a filtered delete.
/// Together they are what lets a document exist as nothing but a set of chunks sharing a
/// <c>documentId</c> — no documents table, no second lifecycle (D1).
/// </summary>
public class VectorScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferhub-scan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ScanReturnsEveryRecordOrderedByIdWhenUnfiltered()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("c", [1f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("b", [1f, 0f]));

        var entries = await store.ScanAsync("docs", filter: null, limit: 10);

        Assert.Equal(["a", "b", "c"], entries.Select(e => e.Id));
    }

    [Fact]
    public async Task ScanFiltersOnEveryMetadataKey()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("x1", [1f, 0f], Metadata: Meta("doc", "handbook")));
        await store.UpsertAsync("docs", new VectorUpsert("x2", [1f, 0f], Metadata: Meta("doc", "handbook")));
        await store.UpsertAsync("docs", new VectorUpsert("y1", [1f, 0f], Metadata: Meta("doc", "policy")));
        await store.UpsertAsync("docs", new VectorUpsert("z1", [1f, 0f])); // no metadata at all

        var entries = await store.ScanAsync("docs", Meta("doc", "handbook"), limit: 10);

        Assert.Equal(["x1", "x2"], entries.Select(e => e.Id));
    }

    [Fact]
    public async Task ScanPagesWithTheAfterIdCursor()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            await store.UpsertAsync("docs", new VectorUpsert(id, [1f, 0f]));
        }

        var first = await store.ScanAsync("docs", null, limit: 2);
        var second = await store.ScanAsync("docs", null, limit: 2, afterId: first[^1].Id);
        var third = await store.ScanAsync("docs", null, limit: 2, afterId: second[^1].Id);

        Assert.Equal(["a", "b"], first.Select(e => e.Id));
        Assert.Equal(["c", "d"], second.Select(e => e.Id));
        Assert.Equal(["e"], third.Select(e => e.Id));
    }

    [Fact]
    public async Task ScanCarriesPayloadAndMetadataButNotTheEmbedding()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert(
            "a", [0.25f, 0.75f],
            Payload: System.Text.Json.JsonSerializer.SerializeToElement(new { text = "hello" }),
            Metadata: Meta("doc", "one")));

        var entry = Assert.Single(await store.ScanAsync("docs", null, limit: 10));

        Assert.Equal("hello", entry.Payload!.Value.GetProperty("text").GetString());
        Assert.Equal("one", entry.Metadata!["doc"]);
        Assert.True(entry.SeqNo > 0);
    }

    [Fact]
    public async Task DeleteByFilterRemovesOnlyTheMatchingRecords()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("x1", [1f, 0f], Metadata: Meta("doc", "handbook")));
        await store.UpsertAsync("docs", new VectorUpsert("x2", [1f, 0f], Metadata: Meta("doc", "handbook")));
        await store.UpsertAsync("docs", new VectorUpsert("y1", [1f, 0f], Metadata: Meta("doc", "policy")));

        var deleted = await store.DeleteByFilterAsync("docs", Meta("doc", "handbook"));

        Assert.Equal(2, deleted);
        var info = await store.GetCollectionAsync("docs");
        Assert.Equal(1, info!.RecordCount);
        Assert.NotNull(await store.GetAsync("docs", "y1"));
    }

    [Fact]
    public async Task DeleteByFilterRaisesRecordDeletedSoNodeReplicasFollow()
    {
        // The replicas are derived, and they only learn about a delete through this event. A bulk
        // path that skipped it would leave every node in the fleet still serving the chunks of a
        // document the hub thinks is gone — and a node replica answers reads before the hub does.
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("x1", [1f, 0f], Metadata: Meta("doc", "gone")));
        await store.UpsertAsync("docs", new VectorUpsert("x2", [1f, 0f], Metadata: Meta("doc", "gone")));

        var replicated = new List<string>();
        store.RecordDeleted += (_, id, _, _) => replicated.Add(id);

        await store.DeleteByFilterAsync("docs", Meta("doc", "gone"));

        Assert.Equal(["x1", "x2"], replicated.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task DeleteByFilterSurvivesARestartBecauseItWentThroughTheRawStore()
    {
        using (var store = NewStore())
        {
            await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
            await store.UpsertAsync("docs", new VectorUpsert("x1", [1f, 0f], Metadata: Meta("doc", "gone")));
            await store.UpsertAsync("docs", new VectorUpsert("y1", [1f, 0f], Metadata: Meta("doc", "stays")));
            await store.DeleteByFilterAsync("docs", Meta("doc", "gone"));
        }

        using var reopened = NewStore();

        Assert.Null(await reopened.GetAsync("docs", "x1"));
        Assert.NotNull(await reopened.GetAsync("docs", "y1"));
    }

    [Fact]
    public async Task AnEmptyFilterIsRefusedRatherThanTreatedAsDeleteEverything()
    {
        using var store = NewStore();
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("x1", [1f, 0f]));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.DeleteByFilterAsync("docs", new Dictionary<string, string>()));

        var info = await store.GetCollectionAsync("docs");
        Assert.Equal(1, info!.RecordCount);
    }

    [Fact]
    public async Task ScanningAMissingCollectionIsANotFound()
    {
        using var store = NewStore();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.ScanAsync("ghost", null, limit: 10));
    }

    private static Dictionary<string, string> Meta(string key, string value) => new() { [key] = value };

    private LocalVectorStore NewStore()
    {
        var opts = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = _root,
            Distance = "cosine"
        });
        return new LocalVectorStore(opts, NullLogger<LocalVectorStore>.Instance);
    }
}
