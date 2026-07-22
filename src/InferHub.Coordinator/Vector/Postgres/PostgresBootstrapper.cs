using InferHub.Coordinator.Postgres;
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
            // Every statement below is `IF NOT EXISTS`, and none of them are atomic — under HA
            // (v3.0) two coordinators bootstrap the same empty database at the same instant, and
            // the loser used to die on a catalog unique index. See ConcurrentDdl.
            if (_pg.AutoCreateExtension)
            {
                try
                {
                    await ConcurrentDdl.RunAsync(
                        ct => ExecuteAsync(conn, "CREATE EXTENSION IF NOT EXISTS vector", ct),
                        logger, "the vector extension", cancellationToken);
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
                await ConcurrentDdl.RunAsync(
                    ct => ExecuteAsync(conn, $"CREATE SCHEMA IF NOT EXISTS {PostgresSchema.QuoteIdent(_pg.Schema)}", ct),
                    logger, $"schema '{_pg.Schema}'", cancellationToken);
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

            await ConcurrentDdl.RunAsync(
                ct => ExecuteAsync(conn, PostgresSchema.CreateRegistryTableSql(_pg.Schema), ct),
                logger, "the collection registry table", cancellationToken);

            var count = await store.LoadRegistryCacheAsync(cancellationToken);

            // Bring collections created before v2.6 up to hybrid search — add the keyword column and
            // index where they're missing. Idempotent, and no re-embedding: the column is generated
            // from the payload text already stored.
            await ConcurrentDdl.RunAsync(
                store.EnsureKeywordIndexesAsync,
                logger, "the keyword indexes", cancellationToken);

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
