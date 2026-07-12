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
