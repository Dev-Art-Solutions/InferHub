using InferHub.Shared.Vector;
using Npgsql;

namespace InferHub.Shared.Postgres;

/// <summary>
/// The DDL sequence that prepares a Postgres+pgvector database for <see cref="PostgresVectorStore"/>:
/// the extension, the schema, the registry table, the pre-hybrid-search keyword-index backfill, and
/// warming the store's metadata cache.
/// </summary>
/// <remarks>
/// Phase 71 extracted this from the coordinator's own <c>PostgresBootstrapper</c> so a node running
/// <c>LocalApi:Retrieval:Provider=postgres</c> runs the identical sequence instead of a second one —
/// the phase-38 D2 / phase-44 D2 discipline applied to bootstrap DDL rather than retrieval. It takes
/// an already-open <see cref="NpgsqlConnection"/> rather than a data source: opening the connection
/// is the one step whose failure message differs by host (each names its own configuration key), so
/// each host keeps that one line and calls this for everything after it succeeds.
/// </remarks>
public static class PostgresBootstrap
{
    public static async Task<PostgresBootstrapResult> RunAsync(
        NpgsqlConnection connection,
        PostgresVectorStore store,
        PostgresStoreOptions options,
        IVectorLog log,
        CancellationToken cancellationToken)
    {
        // Every statement below is `IF NOT EXISTS`, and none of them are atomic — see ConcurrentDdl.
        if (options.AutoCreateExtension)
        {
            try
            {
                await ConcurrentDdl.RunAsync(
                    ct => ExecuteAsync(connection, "CREATE EXTENSION IF NOT EXISTS vector", ct),
                    log, "the vector extension", cancellationToken);
            }
            catch (PostgresException ex)
            {
                throw new InvalidOperationException(
                    "Failed to CREATE EXTENSION vector. The DB role lacks the privilege — have a DBA run " +
                    "'CREATE EXTENSION vector' once, then disable auto-create for this deployment. " +
                    $"Underlying error: {ex.MessageText}", ex);
            }
        }

        if (options.AutoCreateSchema)
        {
            await ConcurrentDdl.RunAsync(
                ct => ExecuteAsync(connection, $"CREATE SCHEMA IF NOT EXISTS {PostgresSchema.QuoteIdent(options.Schema)}", ct),
                log, $"schema '{options.Schema}'", cancellationToken);
        }

        // pgvector's types were registered on the data source builder; reload so the just-created
        // extension's types are picked up on this connection.
        await connection.ReloadTypesAsync(cancellationToken);

        var version = await ScalarAsync(connection, "SELECT extversion FROM pg_extension WHERE extname = 'vector'", cancellationToken);
        if (version is null)
        {
            throw new InvalidOperationException(
                "The pgvector extension is not installed in this database and auto-create did not create it. " +
                "Install it (CREATE EXTENSION vector) or enable Postgres:AutoCreateExtension.");
        }

        await ConcurrentDdl.RunAsync(
            ct => ExecuteAsync(connection, PostgresSchema.CreateRegistryTableSql(options.Schema), ct),
            log, "the collection registry table", cancellationToken);

        var count = await store.LoadRegistryCacheAsync(cancellationToken);

        // Bring collections created before v2.6 up to hybrid search — idempotent, no re-embedding.
        await ConcurrentDdl.RunAsync(store.EnsureKeywordIndexesAsync, log, "the keyword indexes", cancellationToken);

        log.Info(
            "Postgres vector store ready (schema={Schema}, collections={Count}, pgvector={Version})",
            options.Schema, count, version);

        return new PostgresBootstrapResult(count, version);
    }

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

public sealed record PostgresBootstrapResult(int CollectionCount, string PgVectorVersion);
