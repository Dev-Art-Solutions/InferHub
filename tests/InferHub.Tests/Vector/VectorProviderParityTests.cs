using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Postgres;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Tests.Vector;

/// <summary>
/// Proves "same contract, different engine": the same dataset run through
/// <see cref="LocalVectorStore"/> and <see cref="PostgresVectorStore"/> yields identical id
/// ordering and scores within 1e-4, for every distance metric. Gated on
/// <c>INFERHUB_TEST_POSTGRES</c>; skipped visibly when unset.
/// </summary>
[Collection("postgres")]
public class VectorProviderParityTests : IAsyncLifetime
{
    private static readonly string? ConnString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES");
    private const string TestSchema = "inferhub_test";

    private string _root = null!;
    private NpgsqlDataSource? _dataSource;
    private PostgresVectorStore _pg = null!;
    private readonly List<string> _created = new();

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        if (ConnString is null) return;

        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            Provider = "postgres",
            Distance = "cosine",
            Postgres = new PostgresStoreOptions { ConnectionString = ConnString, Schema = TestSchema }
        });

        var dsb = new NpgsqlDataSourceBuilder(ConnString);
        dsb.UseVector();
        _dataSource = dsb.Build();
        _pg = new PostgresVectorStore(_dataSource, options, NullLogger<PostgresVectorStore>.Instance);
        var boot = new PostgresBootstrapper(_dataSource, _pg, options, NullLogger<PostgresBootstrapper>.Instance);
        await boot.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            foreach (var name in _created)
            {
                try { await _pg.DropCollectionAsync(name); } catch { /* best-effort */ }
            }
            await _dataSource.DisposeAsync();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [PostgresTheory]
    [InlineData("cosine")]
    [InlineData("dot")]
    [InlineData("l2")]
    public async Task SameOrderingAndScoresAcrossProviders(string distance)
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

        var pgName = "p_" + Guid.NewGuid().ToString("N")[..12];
        _created.Add(pgName);
        await _pg.CreateCollectionAsync(pgName, 3, distance);

        foreach (var (id, vector) in data)
        {
            await local.UpsertAsync("docs", new VectorUpsert(id, vector));
            await _pg.UpsertAsync(pgName, new VectorUpsert(id, vector));
        }

        var localMatches = await local.QueryAsync("docs", new VectorQuery(query, K: 5));
        var pgMatches = await _pg.QueryAsync(pgName, new VectorQuery(query, K: 5));

        Assert.Equal(localMatches.Select(m => m.Id).ToArray(), pgMatches.Select(m => m.Id).ToArray());
        for (var i = 0; i < localMatches.Count; i++)
        {
            Assert.Equal(localMatches[i].Score, pgMatches[i].Score, precision: 4);
        }
    }

    /// <summary>
    /// Phase 23's seam has to mean the same thing under both engines, because the document model is
    /// built entirely on top of it: a scan that paged differently, or a filtered delete that matched
    /// differently, would give the two providers two different ideas of what a document *is*.
    /// </summary>
    [PostgresFact]
    public async Task ScanAndFilteredDeleteAgreeAcrossProviders()
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

        var pgName = "p_" + Guid.NewGuid().ToString("N")[..12];
        _created.Add(pgName);
        await _pg.CreateCollectionAsync(pgName, 3, "cosine");

        foreach (var (id, doc) in data)
        {
            var upsert = new VectorUpsert(id, [1f, 0f, 0f], Metadata: new Dictionary<string, string> { ["documentId"] = doc });
            await local.UpsertAsync("docs", upsert);
            await _pg.UpsertAsync(pgName, upsert);
        }

        var filter = new Dictionary<string, string> { ["documentId"] = "handbook" };

        // Unfiltered scan: same records, same id order.
        var localAll = await local.ScanAsync("docs", null, limit: 10);
        var pgAll = await _pg.ScanAsync(pgName, null, limit: 10);
        Assert.Equal(localAll.Select(e => e.Id).ToArray(), pgAll.Select(e => e.Id).ToArray());

        // Same cursor semantics: afterId is exclusive, ordering is by id.
        var localPage = await local.ScanAsync("docs", null, limit: 2, afterId: localAll[0].Id);
        var pgPage = await _pg.ScanAsync(pgName, null, limit: 2, afterId: pgAll[0].Id);
        Assert.Equal(localPage.Select(e => e.Id).ToArray(), pgPage.Select(e => e.Id).ToArray());

        // Same filter semantics.
        var localFiltered = await local.ScanAsync("docs", filter, limit: 10);
        var pgFiltered = await _pg.ScanAsync(pgName, filter, limit: 10);
        Assert.Equal(["a1", "c3", "d4"], localFiltered.Select(e => e.Id).ToArray());
        Assert.Equal(localFiltered.Select(e => e.Id).ToArray(), pgFiltered.Select(e => e.Id).ToArray());

        // Same delete semantics, and the same count back.
        Assert.Equal(3, await local.DeleteByFilterAsync("docs", filter));
        Assert.Equal(3, await _pg.DeleteByFilterAsync(pgName, filter));

        Assert.Equal(["b2"], (await local.ScanAsync("docs", null, limit: 10)).Select(e => e.Id).ToArray());
        Assert.Equal(["b2"], (await _pg.ScanAsync(pgName, null, limit: 10)).Select(e => e.Id).ToArray());
    }

    private LocalVectorStore NewLocal(string distance)
    {
        var opts = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N")),
            Distance = distance,
            SnapshotEveryOps = 5000
        });
        return new LocalVectorStore(opts, NullLogger<LocalVectorStore>.Instance);
    }
}
