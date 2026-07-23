using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Qdrant;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

/// <summary>
/// The Qdrant store against a real Qdrant, gated on <c>INFERHUB_TEST_QDRANT</c>. Mirrors the
/// behaviours <see cref="LocalVectorStore"/> is held to, and proves "same contract, different
/// engine" against the local store directly (the parity arm for phase 33). Each test uses a unique
/// collection so runs don't collide, and drops it afterwards.
/// </summary>
public class QdrantVectorStoreTests : IAsyncLifetime
{
    private static readonly string? Url = QdrantTestGate.Url;
    private readonly string _prefix = "t_" + Guid.NewGuid().ToString("N")[..12] + "_";
    private readonly List<string> _created = new();
    private string _local = null!;

    public Task InitializeAsync()
    {
        _local = Path.Combine(Path.GetTempPath(), "inferhub-qdrant-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_local);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Url is not null)
        {
            var store = NewStore("cosine");
            foreach (var name in _created)
            {
                try { await store.DropCollectionAsync(name); } catch { /* best-effort */ }
            }
        }
        if (Directory.Exists(_local)) Directory.Delete(_local, recursive: true);
    }

    [QdrantFact]
    public async Task CreateGetDrop()
    {
        var store = NewStore("cosine");
        var name = NewName();

        var created = await store.CreateCollectionAsync(name, 3, "cosine");
        Assert.Equal(name, created.Name);
        Assert.Equal(3, created.Dimension);
        Assert.Equal("cosine", created.Distance);

        var info = await store.GetCollectionAsync(name);
        Assert.NotNull(info);
        Assert.Equal(3, info!.Dimension);

        Assert.True(await store.DropCollectionAsync(name));
        Assert.Null(await store.GetCollectionAsync(name));
        Assert.False(await store.DropCollectionAsync(name));
    }

    [QdrantFact]
    public async Task UpsertGetAndReplaceById()
    {
        var store = NewStore("cosine");
        var name = NewName();
        await store.CreateCollectionAsync(name, 3, "cosine");

        var meta = new Dictionary<string, string> { ["documentId"] = "d1" };
        await store.UpsertAsync(name, new VectorUpsert("x", [1f, 0f, 0f], Metadata: meta));

        var got = await store.GetAsync(name, "x");
        Assert.NotNull(got);
        Assert.Equal("x", got!.Id);
        Assert.Equal([1f, 0f, 0f], got.Vector);
        Assert.Equal("d1", got.Metadata!["documentId"]);

        // Re-upsert the same id replaces rather than duplicating.
        await store.UpsertAsync(name, new VectorUpsert("x", [0f, 1f, 0f]));
        var replaced = await store.GetAsync(name, "x");
        Assert.Equal([0f, 1f, 0f], replaced!.Vector);

        var count = (await store.GetCollectionAsync(name))!.RecordCount;
        Assert.Equal(1, count);
    }

    [QdrantFact]
    public async Task DimensionMismatchThrows()
    {
        var store = NewStore("cosine");
        var name = NewName();
        await store.CreateCollectionAsync(name, 3, "cosine");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(name, new VectorUpsert("x", [1f, 0f])));
    }

    [QdrantFact]
    public async Task DeleteReturnsFalseForUnknownAndTrueForExisting()
    {
        var store = NewStore("cosine");
        var name = NewName();
        await store.CreateCollectionAsync(name, 3, "cosine");

        Assert.False(await store.DeleteAsync(name, "nope"));

        await store.UpsertAsync(name, new VectorUpsert("x", [1f, 0f, 0f]));
        Assert.True(await store.DeleteAsync(name, "x"));
        Assert.Null(await store.GetAsync(name, "x"));
    }

    [QdrantFact]
    public async Task UnknownCollectionThrowsKeyNotFound()
    {
        var store = NewStore("cosine");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetAsync("does-not-exist", "x"));
    }

    [QdrantTheory]
    [InlineData("cosine")]
    [InlineData("dot")]
    [InlineData("l2")]
    public async Task SameOrderingAndScoresAsLocal(string distance)
    {
        var data = new (string Id, float[] Vector)[]
        {
            ("a", [1f, 0f, 0f]),
            ("b", [0f, 1f, 0f]),
            ("c", [0.9f, 0.1f, 0.1f]),
            ("d", [0.2f, 0.8f, 0.1f]),
            ("e", [0.5f, 0.5f, 0.5f]),
        };
        var query = new float[] { 1f, 0f, 0f };

        using var local = NewLocal(distance);
        await local.CreateCollectionAsync("docs", 3, distance);

        var qstore = NewStore(distance);
        var name = NewName();
        await qstore.CreateCollectionAsync(name, 3, distance);

        foreach (var (id, vector) in data)
        {
            await local.UpsertAsync("docs", new VectorUpsert(id, vector));
            await qstore.UpsertAsync(name, new VectorUpsert(id, vector));
        }

        var localMatches = await local.QueryAsync("docs", new VectorQuery(query, K: 5));
        var qMatches = await qstore.QueryAsync(name, new VectorQuery(query, K: 5));

        Assert.Equal(localMatches.Select(m => m.Id).ToArray(), qMatches.Select(m => m.Id).ToArray());
        for (var i = 0; i < localMatches.Count; i++)
        {
            Assert.Equal(localMatches[i].Score, qMatches[i].Score, precision: 4);
        }
    }

    [QdrantFact]
    public async Task FilterNarrowsAndNullMetadataIsExcluded()
    {
        var store = NewStore("cosine");
        var name = NewName();
        await store.CreateCollectionAsync(name, 3, "cosine");

        await store.UpsertAsync(name, new VectorUpsert("m", [1f, 0f, 0f], Metadata: new Dictionary<string, string> { ["doc"] = "keep" }));
        await store.UpsertAsync(name, new VectorUpsert("n", [1f, 0f, 0f], Metadata: new Dictionary<string, string> { ["doc"] = "other" }));
        await store.UpsertAsync(name, new VectorUpsert("o", [1f, 0f, 0f])); // no metadata

        var filter = new Dictionary<string, string> { ["doc"] = "keep" };
        var matches = await store.QueryAsync(name, new VectorQuery([1f, 0f, 0f], K: 10, Filter: filter));

        Assert.Equal(["m"], matches.Select(x => x.Id).ToArray());
    }

    [QdrantFact]
    public async Task ScanIsOrderedByIdWithExclusiveCursorAndFilteredDeleteAgreesWithLocal()
    {
        var data = new (string Id, string Doc)[]
        {
            ("c3", "handbook"),
            ("a1", "handbook"),
            ("b2", "policy"),
            ("d4", "handbook"),
        };

        using var local = NewLocal("cosine");
        await local.CreateCollectionAsync("docs", 3, "cosine");

        var qstore = NewStore("cosine");
        var name = NewName();
        await qstore.CreateCollectionAsync(name, 3, "cosine");

        foreach (var (id, doc) in data)
        {
            var upsert = new VectorUpsert(id, [1f, 0f, 0f], Metadata: new Dictionary<string, string> { ["documentId"] = doc });
            await local.UpsertAsync("docs", upsert);
            await qstore.UpsertAsync(name, upsert);
        }

        var localAll = await local.ScanAsync("docs", null, limit: 10);
        var qAll = await qstore.ScanAsync(name, null, limit: 10);
        Assert.Equal(localAll.Select(e => e.Id).ToArray(), qAll.Select(e => e.Id).ToArray());

        // afterId is exclusive, ordering is by id.
        var qPage = await qstore.ScanAsync(name, null, limit: 2, afterId: qAll[0].Id);
        Assert.Equal(localAll.Skip(1).Take(2).Select(e => e.Id).ToArray(), qPage.Select(e => e.Id).ToArray());

        var filter = new Dictionary<string, string> { ["documentId"] = "handbook" };
        var qFiltered = await qstore.ScanAsync(name, filter, limit: 10);
        Assert.Equal(["a1", "c3", "d4"], qFiltered.Select(e => e.Id).ToArray());

        Assert.Equal(3, await qstore.DeleteByFilterAsync(name, filter));
        Assert.Equal(["b2"], (await qstore.ScanAsync(name, null, limit: 10)).Select(e => e.Id).ToArray());
    }

    [QdrantFact]
    public async Task DeleteByFilterRefusesEmptyFilter()
    {
        var store = NewStore("cosine");
        var name = NewName();
        await store.CreateCollectionAsync(name, 3, "cosine");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteByFilterAsync(name, new Dictionary<string, string>()));
    }

    private string NewName()
    {
        var name = "c" + Guid.NewGuid().ToString("N")[..10];
        _created.Add(name);
        return name;
    }

    private QdrantVectorStore NewStore(string distance)
    {
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            Provider = "qdrant",
            Distance = distance,
            Qdrant = new QdrantStoreOptions { Url = Url!, CollectionPrefix = _prefix }
        });
        var http = QdrantClient.Configure(new HttpClient(), Url!, null, 30);
        return new QdrantVectorStore(new QdrantClient(http), options, NullLogger<QdrantVectorStore>.Instance);
    }

    private LocalVectorStore NewLocal(string distance)
    {
        var opts = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = Path.Combine(_local, Guid.NewGuid().ToString("N")),
            Distance = distance,
            SnapshotEveryOps = 5000
        });
        return new LocalVectorStore(opts, NullLogger<LocalVectorStore>.Instance);
    }
}
