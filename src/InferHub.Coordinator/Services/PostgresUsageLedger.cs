using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The opt-in durable ledger (phase 25, D2): one append-only table, reached over its own
/// connection string, reusing <c>Npgsql</c> which is already a recorded dependency — nothing
/// new was added. Rows are inserted one at a time as requests complete; usage volume is
/// request volume, and a batching layer would be complexity spent on a problem the deployment
/// does not have. The table is created on first use, in the vector store's auto-migration
/// spirit.
///
/// What a row contains is <see cref="UsageRecord"/> and nothing more — no prompt, no
/// completion, no hash, no sample (D3/rule 7). If a column for content ever seems useful,
/// re-read the design decision instead of adding it.
/// </summary>
public sealed class PostgresUsageLedger : IUsageLedger, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly PostgresUsageOptions options;
    private readonly ILogger<PostgresUsageLedger> logger;
    private readonly SemaphoreSlim bootstrapGate = new(1, 1);
    private volatile bool bootstrapped;

    public PostgresUsageLedger(IOptions<UsageOptions> usageOptions, ILogger<PostgresUsageLedger> logger)
    {
        options = usageOptions.Value.Postgres;
        this.logger = logger;
        dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    private string QualifiedTable =>
        Quote(options.Schema) + "." + Quote(options.Table);

    public async ValueTask RecordAsync(UsageRecord record, CancellationToken cancellationToken = default)
    {
        await EnsureBootstrappedAsync(cancellationToken);

        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {QualifiedTable}
                (at_utc, client_id, model, kind, prompt_tokens, completion_tokens, fallback)
            VALUES (@at, @client, @model, @kind, @prompt, @completion, @fallback)
            """);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("at", record.AtUtc);
        command.Parameters.AddWithValue("client", record.ClientId);
        command.Parameters.AddWithValue("model", record.Model);
        command.Parameters.AddWithValue("kind", record.Kind);
        command.Parameters.AddWithValue("prompt", record.PromptTokens);
        command.Parameters.AddWithValue("completion", record.CompletionTokens);
        command.Parameters.AddWithValue("fallback", record.Fallback);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UsageAggregate>> QueryAsync(UsageQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureBootstrappedAsync(cancellationToken);

        var conditions = new List<string>();
        await using var command = dataSource.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        if (query.FromUtc is { } from)
        {
            conditions.Add("at_utc >= @from");
            command.Parameters.AddWithValue("from", from);
        }

        if (query.ToUtc is { } to)
        {
            conditions.Add("at_utc < @to");
            command.Parameters.AddWithValue("to", to);
        }

        if (!string.IsNullOrEmpty(query.ClientId))
        {
            conditions.Add("lower(client_id) = lower(@client)");
            command.Parameters.AddWithValue("client", query.ClientId);
        }

        if (!string.IsNullOrEmpty(query.Model))
        {
            conditions.Add("lower(model) = lower(@model)");
            command.Parameters.AddWithValue("model", query.Model);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        command.CommandText =
            $"""
            SELECT client_id, model,
                   count(*)                                    AS requests,
                   coalesce(sum(prompt_tokens), 0)             AS prompt_tokens,
                   coalesce(sum(completion_tokens), 0)         AS completion_tokens,
                   count(*) FILTER (WHERE fallback)            AS fallback_requests
            FROM {QualifiedTable}
            {where}
            GROUP BY client_id, model
            ORDER BY lower(client_id), lower(model)
            """;

        var rows = new List<UsageAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UsageAggregate(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    private async Task EnsureBootstrappedAsync(CancellationToken cancellationToken)
    {
        if (bootstrapped)
        {
            return;
        }

        await bootstrapGate.WaitAsync(cancellationToken);
        try
        {
            if (bootstrapped)
            {
                return;
            }

            await using var command = dataSource.CreateCommand(
                $"""
                CREATE SCHEMA IF NOT EXISTS {Quote(options.Schema)};
                CREATE TABLE IF NOT EXISTS {QualifiedTable} (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    at_utc timestamptz NOT NULL,
                    client_id text NOT NULL,
                    model text NOT NULL,
                    kind text NOT NULL,
                    prompt_tokens bigint NOT NULL,
                    completion_tokens bigint NOT NULL,
                    fallback boolean NOT NULL
                );
                CREATE INDEX IF NOT EXISTS {Quote(options.Table + "_at_client_idx")}
                    ON {QualifiedTable} (at_utc, client_id);
                """);
            command.CommandTimeout = options.CommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync(cancellationToken);

            bootstrapped = true;
            logger.LogInformation("Usage ledger table {Table} is ready", QualifiedTable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Failed to prepare the PostgreSQL usage ledger. Check Usage:Postgres:ConnectionString " +
                $"(set it via env Usage__Postgres__ConnectionString or user-secrets). Underlying error: {ex.Message}", ex);
        }
        finally
        {
            bootstrapGate.Release();
        }
    }

    // Schema and table names are validated as ^[a-z_][a-z0-9_]*$ at startup; quoting is a
    // second belt on the same trousers.
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
