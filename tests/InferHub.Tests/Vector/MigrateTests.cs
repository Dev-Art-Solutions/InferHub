using System.Text.Json;
using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Postgres;
using InferHub.Coordinator.Vector.Qdrant;
using InferHub.Migrate;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Tests.Vector;

/// <summary>
/// The phase-35 migration tool. The ungated arms run local → local, which is enough to hold the
/// contract that matters — ids, vectors, payloads, metadata and query results all survive the copy,
/// a dry run writes nothing, a re-run converges, and a shape mismatch is refused rather than
/// half-applied. The cross-provider arms are gated on the same env vars as the provider tests: the
/// tool is written entirely against <see cref="IVectorStore"/>, so a provider pair that works is
/// evidence for every pair, but the pairs people will actually run are pinned anyway.
/// </summary>
public class MigrateTests : IAsyncLifetime
{
    private string _root = null!;
    private readonly List<Func<Task>> _cleanup = [];

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var clean in _cleanup)
        {
            try { await clean(); } catch { /* best-effort */ }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CopiesACollectionFaithfully()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);

        var results = await new Migrator(source, target).RunAsync(new MigrationOptions(BatchSize: 2));

        var result = Assert.Single(results);
        Assert.Null(result.Skipped);
        Assert.True(result.TargetCreated);
        Assert.Equal(5, result.SourceCount);
        Assert.Equal(5, result.Copied);
        Assert.Equal(5, result.TargetCount);

        var info = await target.GetCollectionAsync("docs");
        Assert.Equal(3, info!.Dimension);
        Assert.Equal("cosine", info.Distance);

        // Every field a caller can observe survives: id, vector, payload, metadata.
        foreach (var original in await source.ScanWithVectorsAsync("docs", null, 100))
        {
            var copy = await target.GetAsync("docs", original.Id);
            Assert.NotNull(copy);
            Assert.Equal(original.Vector, copy!.Vector);
            Assert.Equal(original.Metadata, copy.Metadata);
            Assert.Equal(original.Payload?.GetRawText(), copy.Payload?.GetRawText());
        }
    }

    [Fact]
    public async Task QueryAgainstTheTargetReturnsTheSameTopK()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);

        await new Migrator(source, target).RunAsync(new MigrationOptions());

        var query = new VectorQuery([1f, 0f, 0f], K: 5);
        var before = await source.QueryAsync("docs", query);
        var after = await target.QueryAsync("docs", query);

        Assert.Equal(before.Select(m => m.Id), after.Select(m => m.Id));
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Score, after[i].Score, precision: 4);
        }
    }

    [Fact]
    public async Task DryRunReportsThePlanAndWritesNothing()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);

        var results = await new Migrator(source, target).RunAsync(new MigrationOptions(DryRun: true));

        var result = Assert.Single(results);
        Assert.Equal(5, result.SourceCount);
        Assert.Equal(0, result.Copied);
        Assert.True(result.TargetCreated);   // *would* create

        Assert.Null(await target.GetCollectionAsync("docs"));
        Assert.Empty(await target.ListCollectionsAsync());
    }

    [Fact]
    public async Task ReRunningConvergesInsteadOfDuplicating()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);

        var migrator = new Migrator(source, target);
        await migrator.RunAsync(new MigrationOptions());
        var second = await migrator.RunAsync(new MigrationOptions());

        var result = Assert.Single(second);
        Assert.False(result.TargetCreated);        // the collection was already there
        Assert.Equal(5, result.Copied);
        Assert.Equal(5, result.TargetCount);       // and it still holds exactly five records
    }

    [Fact]
    public async Task ACollectionTheTargetHoldsWithADifferentShapeIsSkipped()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);
        await target.CreateCollectionAsync("docs", 8, "cosine");

        var results = await new Migrator(source, target).RunAsync(new MigrationOptions());

        var result = Assert.Single(results);
        Assert.NotNull(result.Skipped);
        Assert.Contains("dimension", result.Skipped);
        Assert.Equal(0, result.Copied);
        // Refused, not half-applied: nothing was written into the mismatched collection.
        Assert.Equal(0, (await target.GetCollectionAsync("docs"))!.RecordCount);
    }

    [Fact]
    public async Task ADistanceMismatchIsAlsoRefused()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);
        await target.CreateCollectionAsync("docs", 3, "l2");

        var result = Assert.Single(await new Migrator(source, target).RunAsync(new MigrationOptions()));

        Assert.NotNull(result.Skipped);
        Assert.Contains("distance", result.Skipped);
        Assert.Equal(0, result.Copied);
    }

    [Fact]
    public async Task NamingACollectionCopiesOnlyThatOne()
    {
        using var source = NewLocal();
        using var target = NewLocal();
        await SeedAsync(source);
        await source.CreateCollectionAsync("other", 3, "cosine");
        await source.UpsertAsync("other", new VectorUpsert("z", [0f, 0f, 1f]));

        var results = await new Migrator(source, target).RunAsync(new MigrationOptions(Collection: "docs"));

        Assert.Equal("docs", Assert.Single(results).Name);
        Assert.Null(await target.GetCollectionAsync("other"));
    }

    [Fact]
    public async Task NamingAMissingCollectionFailsLoudly()
    {
        using var source = NewLocal();
        using var target = NewLocal();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Migrator(source, target).RunAsync(new MigrationOptions(Collection: "nope")));

        Assert.Contains("nope", ex.Message);
    }

    /// <summary>
    /// The whole point of the tool: a populated deployment on one engine, readable on another. Gated
    /// on the Qdrant URL, and it exercises the interesting direction — a local raw store into a real
    /// Qdrant, which is what an operator adopting Qdrant actually has.
    /// </summary>
    [QdrantFact]
    public async Task LocalToQdrantCopiesAndQueriesTheSame()
    {
        using var source = NewLocal();
        await SeedAsync(source);

        var prefix = "m_" + Guid.NewGuid().ToString("N")[..12] + "_";
        var target = NewQdrant(prefix);
        _cleanup.Add(async () => { try { await target.DropCollectionAsync("docs"); } catch { } });

        var result = Assert.Single(await new Migrator(source, target).RunAsync(new MigrationOptions(BatchSize: 2)));
        Assert.Null(result.Skipped);
        Assert.Equal(5, result.Copied);
        Assert.Equal(5, result.TargetCount);

        var query = new VectorQuery([1f, 0f, 0f], K: 5);
        var before = await source.QueryAsync("docs", query);
        var after = await target.QueryAsync("docs", query);
        Assert.Equal(before.Select(m => m.Id), after.Select(m => m.Id));
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Score, after[i].Score, precision: 4);
        }

        // Metadata rode across, so the document model still works on the far side.
        var scanned = await target.ScanAsync("docs", new Dictionary<string, string> { ["documentId"] = "handbook" }, 10);
        Assert.Equal(["a", "c", "e"], scanned.Select(e => e.Id).ToArray());
    }

    [PostgresFact]
    public async Task LocalToPostgresCopiesAndQueriesTheSame()
    {
        using var source = NewLocal();
        await SeedAsync(source);

        var (target, dataSource) = await NewPostgresAsync();
        await using (dataSource)
        {
            // A leftover from a previous run would be shape-compatible and merely converge, but a
            // clean slate is what makes the copied-count assertion mean anything.
            await target.DropCollectionAsync("docs");
            try
            {
                var result = Assert.Single(await new Migrator(source, target).RunAsync(new MigrationOptions(BatchSize: 2)));
                Assert.Null(result.Skipped);
                Assert.Equal(5, result.Copied);
                Assert.Equal(5, result.TargetCount);

                var query = new VectorQuery([1f, 0f, 0f], K: 5);
                var before = await source.QueryAsync("docs", query);
                var after = await target.QueryAsync("docs", query);
                Assert.Equal(before.Select(m => m.Id), after.Select(m => m.Id));
                for (var i = 0; i < before.Count; i++)
                {
                    Assert.Equal(before[i].Score, after[i].Score, precision: 4);
                }
            }
            finally
            {
                try { await target.DropCollectionAsync("docs"); } catch { /* best-effort */ }
            }
        }
    }

    // ---- spec parsing (no store, no server) ------------------------------------------------

    [Theory]
    [InlineData("local:./data/vectors", "local", "VectorStore:DataDirectory", "./data/vectors")]
    [InlineData("qdrant:http://localhost:6333", "qdrant", "VectorStore:Qdrant:Url", "http://localhost:6333")]
    [InlineData("postgres:Host=db;Database=inferhub", "postgres", "VectorStore:Postgres:ConnectionString", "Host=db;Database=inferhub")]
    public void ShorthandSpecsMapToConfigurationKeys(string spec, string provider, string key, string value)
    {
        var config = StoreSpec.Parse(spec, apiKey: null, side: "from");

        Assert.Equal(provider, config["VectorStore:Provider"]);
        Assert.Equal(value, config[key]);
        // A migration side is always enabled, whatever the source config said.
        Assert.Equal("true", config["VectorStore:Enabled"]);
    }

    [Fact]
    public void APostgresUriIsNotMistakenForTheShorthandSeparator()
    {
        var config = StoreSpec.Parse("postgres://user:pw@host:5432/inferhub", apiKey: null, side: "from");

        Assert.Equal("postgres", config["VectorStore:Provider"]);
        Assert.Equal("postgres://user:pw@host:5432/inferhub", config["VectorStore:Postgres:ConnectionString"]);
    }

    [Fact]
    public void AConfigFileIsReadAndTheApiKeyFlagWins()
    {
        var path = Path.Combine(_root, "appsettings.json");
        File.WriteAllText(path, """
            { "VectorStore": { "Enabled": false, "Provider": "qdrant",
              "Qdrant": { "Url": "https://qdrant.internal:6333", "ApiKey": "from-file" } } }
            """);

        var config = StoreSpec.Parse(path, apiKey: "from-flag", side: "to");

        Assert.Equal("qdrant", config["VectorStore:Provider"]);
        Assert.Equal("https://qdrant.internal:6333", config["VectorStore:Qdrant:Url"]);
        Assert.Equal("from-flag", config["VectorStore:Qdrant:ApiKey"]);
        Assert.Equal("true", config["VectorStore:Enabled"]);
    }

    [Fact]
    public void AnUnknownSpecIsAnErrorNamingTheSide()
    {
        var ex = Assert.Throws<ArgumentException>(() => StoreSpec.Parse("milvus:localhost", null, "to"));
        Assert.Contains("--to", ex.Message);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static async Task SeedAsync(IVectorStore store)
    {
        await store.CreateCollectionAsync("docs", 3, "cosine");
        var data = new (string Id, float[] Vector, string Doc)[]
        {
            ("a", [1f, 0f, 0f], "handbook"),
            ("b", [0f, 1f, 0f], "policy"),
            ("c", [0.9f, 0.1f, 0.1f], "handbook"),
            ("d", [0.2f, 0.8f, 0.1f], "policy"),
            ("e", [0.5f, 0.5f, 0.5f], "handbook"),
        };
        foreach (var (id, vector, doc) in data)
        {
            await store.UpsertAsync("docs", new VectorUpsert(
                id,
                vector,
                Payload: JsonSerializer.SerializeToElement(new { text = $"chunk {id} of {doc}" }),
                Metadata: new Dictionary<string, string> { ["documentId"] = doc }));
        }
    }

    private LocalVectorStore NewLocal() => new(
        new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N")),
            Distance = "cosine",
            SnapshotEveryOps = 5000
        },
        NullVectorLog.Instance);

    private static QdrantVectorStore NewQdrant(string prefix)
    {
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            Provider = "qdrant",
            Distance = "cosine",
            Qdrant = new QdrantStoreOptions { Url = QdrantTestGate.Url!, CollectionPrefix = prefix }
        });
        var http = QdrantClient.Configure(new HttpClient(), QdrantTestGate.Url!, null, 30);
        return new QdrantVectorStore(new QdrantClient(http), options, NullLogger<QdrantVectorStore>.Instance);
    }

    private static async Task<(PostgresVectorStore Store, NpgsqlDataSource DataSource)> NewPostgresAsync()
    {
        var connString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES")!;
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            Provider = "postgres",
            Distance = "cosine",
            Postgres = new PostgresStoreOptions { ConnectionString = connString, Schema = "inferhub_migrate_test" }
        });

        var dsb = new NpgsqlDataSourceBuilder(connString);
        dsb.UseVector();
        var dataSource = dsb.Build();
        var store = new PostgresVectorStore(dataSource, options, NullLogger<PostgresVectorStore>.Instance);
        await new PostgresBootstrapper(dataSource, store, options, NullLogger<PostgresBootstrapper>.Instance)
            .StartAsync(CancellationToken.None);
        return (store, dataSource);
    }
}
