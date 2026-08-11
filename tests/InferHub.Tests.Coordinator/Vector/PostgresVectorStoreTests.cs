using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Postgres;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Tests.Vector;

/// <summary>
/// Integration tests for <see cref="PostgresVectorStore"/>, gated on the
/// <c>INFERHUB_TEST_POSTGRES</c> environment variable (an Npgsql connection string). When it is
/// not set every test is dynamically skipped — visibly, not silently. No Testcontainers
/// dependency (design rule 5); run the compose stack in <c>deploy/postgres/</c> instead.
/// Mirrors <c>LocalVectorStoreTests</c> so both providers are held to one contract.
/// </summary>
[Collection("postgres")]
public class PostgresVectorStoreTests : IAsyncLifetime
{
    private static readonly string? ConnString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES");
    private const string TestSchema = "inferhub_test";

    private NpgsqlDataSource? _dataSource;
    private PostgresVectorStore _store = null!;
    private readonly List<string> _created = new();

    public async Task InitializeAsync()
    {
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
        _store = new PostgresVectorStore(_dataSource, options, NullLogger<PostgresVectorStore>.Instance);

        var boot = new PostgresBootstrapper(_dataSource, _store, options, NullLogger<PostgresBootstrapper>.Instance);
        await boot.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is null) return;
        foreach (var name in _created)
        {
            try { await _store.DropCollectionAsync(name); } catch { /* best-effort cleanup */ }
        }
        await _dataSource.DisposeAsync();
    }

    private async Task<string> NewCollection(int dimension, string distance)
    {
        var name = "t_" + Guid.NewGuid().ToString("N")[..16];
        _created.Add(name);
        await _store.CreateCollectionAsync(name, dimension, distance);
        return name;
    }

    [PostgresFact]
    public async Task CreateListGetDropRoundTrip()
    {
        var name = await NewCollection(3, "cosine");

        var listed = await _store.ListCollectionsAsync();
        Assert.Contains(listed, c => c.Name == name && c.Dimension == 3 && c.Distance == "cosine");

        var info = await _store.GetCollectionAsync(name);
        Assert.NotNull(info);
        Assert.Equal(0, info!.RecordCount);

        Assert.True(await _store.DropCollectionAsync(name));
        Assert.Null(await _store.GetCollectionAsync(name));
    }

    [PostgresFact]
    public async Task CreateDuplicateThrowsInvalidOperation()
    {
        var name = await NewCollection(2, "cosine");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateCollectionAsync(name, 2, "cosine"));
    }

    [PostgresFact]
    public async Task UpsertThenGetReturnsRecord()
    {
        var name = await NewCollection(3, "cosine");
        await _store.UpsertAsync(name, new VectorUpsert("a", [1f, 0f, 0f],
            Metadata: new Dictionary<string, string> { ["lang"] = "en" }));

        var record = await _store.GetAsync(name, "a");
        Assert.NotNull(record);
        Assert.Equal("a", record!.Id);
        Assert.Equal(new[] { 1f, 0f, 0f }, record.Vector);
        Assert.Equal("en", record.Metadata!["lang"]);
        Assert.True(record.SeqNo >= 1);
    }

    [PostgresFact]
    public async Task UpsertReplacesById()
    {
        var name = await NewCollection(2, "cosine");
        await _store.UpsertAsync(name, new VectorUpsert("a", [1f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("a", [0f, 1f]));

        var record = await _store.GetAsync(name, "a");
        Assert.Equal(new[] { 0f, 1f }, record!.Vector);

        var info = await _store.GetCollectionAsync(name);
        Assert.Equal(1, info!.RecordCount);
    }

    [PostgresFact]
    public async Task DimensionMismatchOnUpsertThrows()
    {
        var name = await NewCollection(3, "cosine");
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpsertAsync(name, new VectorUpsert("a", [1f, 0f])));
    }

    [PostgresFact]
    public async Task DeleteReturnsFalseWhenIdMissing()
    {
        var name = await NewCollection(2, "cosine");
        Assert.False(await _store.DeleteAsync(name, "ghost"));
    }

    [PostgresFact]
    public async Task DeleteRemovesRecord()
    {
        var name = await NewCollection(2, "cosine");
        await _store.UpsertAsync(name, new VectorUpsert("a", [1f, 0f]));
        Assert.True(await _store.DeleteAsync(name, "a"));
        Assert.Null(await _store.GetAsync(name, "a"));
    }

    [PostgresFact]
    public async Task UnknownCollectionThrowsKeyNotFound()
    {

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _store.UpsertAsync("nope_" + Guid.NewGuid().ToString("N")[..8], new VectorUpsert("a", [1f, 0f])));
    }

    [PostgresFact]
    public async Task CosineQueryReturnsTopKInOrder()
    {
        var name = await NewCollection(3, "cosine");
        await _store.UpsertAsync(name, new VectorUpsert("a", [1f, 0f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("b", [0f, 1f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("c", [0.9f, 0.1f, 0f]));

        var matches = await _store.QueryAsync(name, new VectorQuery([1f, 0f, 0f], K: 2));

        Assert.Equal(2, matches.Count);
        Assert.Equal("a", matches[0].Id);
        Assert.Equal("c", matches[1].Id);
    }

    [PostgresFact]
    public async Task DotQueryReturnsTopKInOrder()
    {
        var name = await NewCollection(2, "dot");
        await _store.UpsertAsync(name, new VectorUpsert("a", [3f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("b", [1f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("c", [5f, 0f]));

        var matches = await _store.QueryAsync(name, new VectorQuery([1f, 0f], K: 3));

        Assert.Equal(new[] { "c", "a", "b" }, matches.Select(m => m.Id).ToArray());
    }

    [PostgresFact]
    public async Task L2QueryReturnsClosestFirst()
    {
        var name = await NewCollection(2, "l2");
        await _store.UpsertAsync(name, new VectorUpsert("close", [0.1f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("mid", [1f, 0f]));
        await _store.UpsertAsync(name, new VectorUpsert("far", [10f, 0f]));

        var matches = await _store.QueryAsync(name, new VectorQuery([0f, 0f], K: 3));

        Assert.Equal(new[] { "close", "mid", "far" }, matches.Select(m => m.Id).ToArray());
    }

    [PostgresFact]
    public async Task MetadataFilterNarrowsResults()
    {
        var name = await NewCollection(2, "cosine");
        await _store.UpsertAsync(name, new VectorUpsert("en", [1f, 0f], Metadata: new Dictionary<string, string> { ["lang"] = "en" }));
        await _store.UpsertAsync(name, new VectorUpsert("bg", [1f, 0f], Metadata: new Dictionary<string, string> { ["lang"] = "bg" }));

        var matches = await _store.QueryAsync(name, new VectorQuery([1f, 0f], K: 5,
            Filter: new Dictionary<string, string> { ["lang"] = "en" }));

        var match = Assert.Single(matches);
        Assert.Equal("en", match.Id);
    }

    [PostgresFact]
    public async Task FilterExcludesRecordsWithNullMetadata()
    {
        var name = await NewCollection(2, "cosine");
        await _store.UpsertAsync(name, new VectorUpsert("tagged", [1f, 0f], Metadata: new Dictionary<string, string> { ["lang"] = "en" }));
        await _store.UpsertAsync(name, new VectorUpsert("untagged", [1f, 0f])); // null metadata

        var matches = await _store.QueryAsync(name, new VectorQuery([1f, 0f], K: 5,
            Filter: new Dictionary<string, string> { ["lang"] = "en" }));

        var match = Assert.Single(matches);
        Assert.Equal("tagged", match.Id);
    }
}
