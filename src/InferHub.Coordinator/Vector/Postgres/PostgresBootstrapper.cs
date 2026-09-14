using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Vector.Postgres;

/// <summary>
/// Prepares the Postgres vector store at startup: ensures the <c>vector</c> extension and the
/// schema exist (when auto-create is on), creates the registry table, and warms the store's
/// metadata cache. Fails fast with an actionable message rather than starting a coordinator
/// that would 500 on every vector call.
/// </summary>
/// <remarks>
/// Phase 71 extracted the DDL sequence itself into
/// <see cref="InferHub.Shared.Postgres.PostgresBootstrap"/> so a node running the same provider
/// runs the identical steps. This class keeps only what is genuinely coordinator-specific: opening
/// the connection with a message naming <c>VectorStore:Postgres:ConnectionString</c>.
/// </remarks>
public sealed class PostgresBootstrapper(
    NpgsqlDataSource dataSource,
    PostgresVectorStore store,
    IOptions<VectorStoreOptions> options,
    ILogger<PostgresBootstrapper> logger) : IVectorStoreBootstrapper
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
            try
            {
                await PostgresBootstrap.RunAsync(
                    conn, store, _pg, new VectorLog<PostgresBootstrapper>(logger), cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is PostgresException pgEx)
            {
                throw new InvalidOperationException(
                    "Failed to CREATE EXTENSION vector. The DB role lacks the privilege — have a DBA run " +
                    "'CREATE EXTENSION vector' once, then set VectorStore:Postgres:AutoCreateExtension=false. " +
                    $"Underlying error: {pgEx.MessageText}", ex);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is null && ex.Message.StartsWith("The pgvector extension", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The pgvector extension is not installed in this database and AutoCreateExtension did not create it. " +
                    "Install it (CREATE EXTENSION vector) or enable VectorStore:Postgres:AutoCreateExtension.", ex);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
