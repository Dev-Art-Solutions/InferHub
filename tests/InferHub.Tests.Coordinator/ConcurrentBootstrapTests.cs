using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Postgres;
using InferHub.Tests.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;

namespace InferHub.Tests;

/// <summary>
/// Two coordinators bootstrapping the same empty database at the same instant (v3.0).
/// </summary>
/// <remarks>
/// <b>`IF NOT EXISTS` is not atomic.</b> The existence check and the catalog insert are separate
/// steps, so racing sessions can both pass the check and one dies on a unique index. Before HA this
/// was unreachable — bootstrap happened once, on the one hub — so nothing covered it.
///
/// <para>v3.0.0 shipped with the race live in the *vector* bootstrapper and the usage ledger: on a
/// two-hub cold start against an empty database, <c>hub-a</c> exited on
/// <c>pg_extension_name_index</c> while <c>hub-b</c> came up fine, and the error text blamed a
/// missing privilege — sending the operator after a DBA for a problem that was a race. Found by
/// running the published images, which is the only place it could be found.</para>
///
/// <para>These tests need a real Postgres and real concurrency; a single-connection test cannot
/// reach the bug. Gated on <c>INFERHUB_TEST_POSTGRES</c> like the other integration suites.</para>
/// </remarks>
[Collection("postgres")]
public class ConcurrentBootstrapTests : IAsyncLifetime
{
    private static readonly string? ConnString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES");

    private readonly string schema = "boot_test_" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<IAsyncDisposable> disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var disposable in disposables)
        {
            await disposable.DisposeAsync();
        }

        if (ConnString is null) return;

        await using var dataSource = new NpgsqlDataSourceBuilder(ConnString).Build();
        await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
        await drop.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task EightCoordinatorsBootstrapTheVectorStoreAtOnce()
    {
        // The exact shape of the v3.0.0 crash: CREATE EXTENSION / CREATE SCHEMA / CREATE TABLE,
        // all IF NOT EXISTS, all raced. One of these used to throw and take its coordinator down.
        var boots = Enumerable.Range(0, 8).Select(_ => BootstrapVectorStoreAsync()).ToArray();

        await Task.WhenAll(boots);
    }

    [PostgresFact]
    public async Task EightCoordinatorsBootstrapTheUsageLedgerAtOnce()
    {
        // Under HA both hubs meter usage, so both reach the ledger bootstrap on their first
        // request against an empty database.
        var ledgers = Enumerable.Range(0, 8).Select(_ =>
        {
            var ledger = new PostgresUsageLedger(
                Options.Create(new UsageOptions
                {
                    Persistence = UsageOptions.PersistencePostgres,
                    Postgres = new PostgresUsageOptions { ConnectionString = ConnString!, Schema = schema }
                }),
                NullLogger<PostgresUsageLedger>.Instance);
            disposables.Add(ledger);
            return ledger;
        }).ToArray();

        await Task.WhenAll(ledgers.Select(l =>
            l.RecordAsync(new UsageRecord("acme", "llama3", "chat", 1, 1, false, DateTimeOffset.UtcNow)).AsTask()));

        var rows = await ledgers[0].QueryAsync(new UsageQuery());
        Assert.Equal(8, Assert.Single(rows).Requests);
    }

    [PostgresFact]
    public async Task EightCoordinatorsBootstrapTheLeaseAtOnce()
    {
        var leases = Enumerable.Range(0, 8).Select(i =>
        {
            var lease = new PostgresClusterLease(
                Options.Create(new ClusterOptions
                {
                    Enabled = true,
                    InstanceId = "hub-" + i,
                    ConnectionString = ConnString!,
                    Schema = schema
                }),
                NullLogger<PostgresClusterLease>.Instance);
            disposables.Add(lease);
            return lease;
        }).ToArray();

        var results = await Task.WhenAll(
            leases.Select(l => l.TryAcquireOrRenewAsync(CancellationToken.None)));

        // Bootstrapping concurrently must not cost the single-holder guarantee.
        Assert.Single(results, r => r.Held);
    }

    private async Task BootstrapVectorStoreAsync()
    {
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            Provider = "postgres",
            Distance = "cosine",
            Postgres = new PostgresStoreOptions { ConnectionString = ConnString!, Schema = schema }
        });

        var builder = new NpgsqlDataSourceBuilder(ConnString);
        builder.UseVector();
        var dataSource = builder.Build();
        disposables.Add(dataSource);

        var store = new PostgresVectorStore(dataSource, options, NullLogger<PostgresVectorStore>.Instance);
        var bootstrapper = new PostgresBootstrapper(
            dataSource, store, options, NullLogger<PostgresBootstrapper>.Instance);

        await bootstrapper.StartAsync(CancellationToken.None);
    }
}
