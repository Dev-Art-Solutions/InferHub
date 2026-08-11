using InferHub.Coordinator.Services;
using InferHub.Tests.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresUsageLedger"/>, gated on
/// <c>INFERHUB_TEST_POSTGRES</c> exactly like the vector-store suite. The ledger table lives in
/// its own throwaway schema per run and is dropped afterwards.
/// </summary>
[Collection("postgres")]
public class PostgresUsageLedgerTests : IAsyncLifetime
{
    private static readonly string? ConnString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES");

    private readonly string schema = "usage_test_" + Guid.NewGuid().ToString("N")[..8];
    private PostgresUsageLedger? ledger;

    public Task InitializeAsync()
    {
        if (ConnString is null) return Task.CompletedTask;

        ledger = new PostgresUsageLedger(
            Options.Create(new UsageOptions
            {
                Persistence = UsageOptions.PersistencePostgres,
                Postgres = new PostgresUsageOptions { ConnectionString = ConnString, Schema = schema }
            }),
            NullLogger<PostgresUsageLedger>.Instance);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (ConnString is null) return;

        await using (var dataSource = new NpgsqlDataSourceBuilder(ConnString).Build())
        await using (var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        if (ledger is not null)
        {
            await ledger.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task RecordsRoundTripThroughTheTable()
    {
        var at = DateTimeOffset.UtcNow;

        await ledger!.RecordAsync(new UsageRecord("acme", "llama3", "chat", 10, 20, false, at));
        await ledger.RecordAsync(new UsageRecord("acme", "llama3", "generate", 5, 5, true, at));
        await ledger.RecordAsync(new UsageRecord("globex", "mistral", "embed", 9, 0, false, at));

        var rows = await ledger.QueryAsync(new UsageQuery());

        Assert.Equal(2, rows.Count);
        var acme = rows.Single(r => r.ClientId == "acme");
        Assert.Equal(2, acme.Requests);
        Assert.Equal(15, acme.PromptTokens);
        Assert.Equal(25, acme.CompletionTokens);
        Assert.Equal(1, acme.FallbackRequests);
    }

    [PostgresFact]
    public async Task QueryFiltersMatchTheInMemoryLedgersSemantics()
    {
        // The two ledgers must agree about what a window and a filter mean, or switching
        // persistence silently changes the invoice.
        var at = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

        await ledger!.RecordAsync(new UsageRecord("acme", "llama3", "chat", 1, 1, false, at));
        await ledger.RecordAsync(new UsageRecord("acme", "llama3", "chat", 2, 2, false, at.AddHours(2)));
        await ledger.RecordAsync(new UsageRecord("globex", "llama3", "chat", 4, 4, false, at));

        var windowed = await ledger.QueryAsync(new UsageQuery(at.AddMinutes(-1), at.AddMinutes(1)));
        Assert.Equal(2, windowed.Count);

        var filtered = Assert.Single(await ledger.QueryAsync(new UsageQuery(ClientId: "ACME")));
        Assert.Equal(3, filtered.PromptTokens); // case-insensitive, both acme rows
    }
}
