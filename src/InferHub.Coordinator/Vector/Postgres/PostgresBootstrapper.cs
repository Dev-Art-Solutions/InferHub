using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Vector.Postgres;

/// <summary>
/// Prepares the Postgres vector store at startup: ensures the <c>vector</c> extension and the
/// schema exist (when auto-create is on), creates the registry table, and warms the store's
/// metadata cache. Fails fast with an actionable message rather than starting a coordinator
/// that would 500 on every vector call.
/// </summary>
public sealed class PostgresBootstrapper(
    NpgsqlDataSource dataSource,
    PostgresVectorStore store,
    IOptions<VectorStoreOptions> options,
    ILogger<PostgresBootstrapper> logger) : IHostedService
{
    private readonly PostgresStoreOptions _pg = options.Value.Postgres;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection conn;
        try
        {
            conn = await dataSource.OpenConnectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to connect to PostgreSQL for the vector store. Check VectorStore:Postgres:ConnectionString " +
                $"(set it via env VectorStore__Postgres__ConnectionString or user-secrets). Underlying error: {ex.Message}", ex);
        }

        await using (conn)
        {
            if (_pg.AutoCreateExtension)
            {
                try
                {
                    await ExecuteAsync(conn, "CREATE EXTENSION IF NOT EXISTS vector", cancellationToken);
                }
                catch (PostgresException ex)
                {
                    throw new InvalidOperationException(
                        "Failed to CREATE EXTENSION vector. The DB role lacks the privilege — have a DBA run " +
                        "'CREATE EXTENSION vector' once, then set VectorStore:Postgres:AutoCreateExtension=false. " +
                        $"Underlying error: {ex.MessageText}", ex);
                }
            }

            if (_pg.AutoCreateSchema)
            {
                await ExecuteAsync(conn, $"CREATE SCHEMA IF NOT EXISTS {PostgresSchema.QuoteIdent(_pg.Schema)}", cancellationToken);
            }

            // pgvector's types were registered on the data source builder; reload so the just-created
            // extension's types are picked up on this connection.
            await conn.ReloadTypesAsync(cancellationToken);

            var version = await ScalarAsync(conn, "SELECT extversion FROM pg_extension WHERE extname = 'vector'", cancellationToken);
            if (version is null)
            {
                throw new InvalidOperationException(
                    "The pgvector extension is not installed in this database and AutoCreateExtension did not create it. " +
                    "Install it (CREATE EXTENSION vector) or enable VectorStore:Postgres:AutoCreateExtension.");
            }

            await ExecuteAsync(conn, PostgresSchema.CreateRegistryTableSql(_pg.Schema), cancellationToken);

            var count = await store.LoadRegistryCacheAsync(cancellationToken);
            logger.LogInformation(
                "Postgres vector store ready (schema={Schema}, collections={Count}, pgvector={Version})",
                _pg.Schema, count, version);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ScalarAsync(NpgsqlConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }
}
