using Npgsql;
using InferHub.Shared.Vector;

namespace InferHub.Shared.Postgres;

/// <summary>
/// Runs bootstrap DDL that two writers may execute at the same instant — a coordinator HA pair, or a
/// coordinator and a node that both happen to bootstrap the same database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately a second copy, not a move of
/// <c>InferHub.Coordinator.Postgres.ConcurrentDdl</c>.</b> That one also serves
/// <c>PostgresClusterLease</c>, <c>PostgresProfileStore</c> and <c>PostgresUsageLedger</c> — three
/// coordinator-only concerns with nothing to do with vectors and no reason to take on the
/// <see cref="IVectorLog"/> seam. This copy is ~20 lines of retry policy around a handful of
/// Postgres error codes, not a ranking algorithm; the risk phase-38 D2 and phase-44 D2 write pages
/// about (a dozen retrieval decisions silently diverging into plausible-but-different answers) does
/// not apply to "retry the DDL statement once more".
/// </para>
/// <para><c>IF NOT EXISTS</c> is not atomic in PostgreSQL: the existence check and the catalog
/// insert are separate steps, so two sessions racing <c>CREATE EXTENSION</c>, <c>CREATE SCHEMA</c>,
/// <c>CREATE TABLE</c> or <c>CREATE INDEX ... IF NOT EXISTS</c> can both pass the check and one then
/// dies on a unique index in <c>pg_extension</c> / <c>pg_namespace</c> / <c>pg_class</c>. The other
/// session winning <b>is</b> success, so the retry simply re-runs the statement: by then the object
/// exists and the <c>IF NOT EXISTS</c> is a no-op.</para>
/// </remarks>
internal static class ConcurrentDdl
{
    private const string UniqueViolation = "23505";
    private const string DuplicateTable = "42P07";
    private const string DuplicateObject = "42710";
    private const string DuplicateSchema = "42P06";

    private const int MaxAttempts = 4;

    internal static bool IsConcurrentCreation(PostgresException ex) =>
        ex.SqlState is UniqueViolation or DuplicateTable or DuplicateObject or DuplicateSchema;

    /// <summary>Execute <paramref name="action"/>, treating a concurrent creator as success.</summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> action,
        IVectorLog log,
        string what,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken);
                return;
            }
            catch (PostgresException ex) when (attempt < MaxAttempts && IsConcurrentCreation(ex))
            {
                log.Debug("Another writer created {What} concurrently ({SqlState}); retrying", what, ex.SqlState);
            }
        }
    }
}
