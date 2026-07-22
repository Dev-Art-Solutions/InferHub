using Npgsql;

namespace InferHub.Coordinator.Postgres;

/// <summary>
/// Runs bootstrap DDL that two coordinators may execute at the same instant.
/// </summary>
/// <remarks>
/// <b>`IF NOT EXISTS` is not atomic in PostgreSQL.</b> The existence check and the catalog insert
/// are separate steps, so two sessions racing <c>CREATE EXTENSION</c>, <c>CREATE SCHEMA</c>,
/// <c>CREATE TABLE</c> or <c>CREATE INDEX ... IF NOT EXISTS</c> can both pass the check and one
/// then dies on a unique index in <c>pg_extension</c> / <c>pg_namespace</c> / <c>pg_class</c>.
///
/// <para>Until v3.0 this was unreachable: bootstrap happened once, on the one coordinator. HA makes
/// simultaneous startup the <i>normal</i> case, and an HA pair that crashes half of itself on a cold
/// boot is not HA. Found in v3.0.0 by pulling the published images and booting two hubs against an
/// empty database — <c>hub-a</c> exited 139 on <c>pg_extension_name_index</c> while <c>hub-b</c>
/// came up fine, and the error text blamed a missing privilege, sending the operator after a DBA
/// for a problem that was a race. That is the D7 discipline earning its keep twice over.</para>
///
/// <para>The other session winning <b>is</b> success, so the retry simply re-runs the statement: by
/// then the object exists and the <c>IF NOT EXISTS</c> is a no-op. Only these states are swallowed,
/// and only for a bounded number of attempts — a genuine privilege error or a typo'd identifier
/// still fails fast and loudly, which is the whole reason the bootstrap is allowed to kill the
/// process at all.</para>
/// </remarks>
internal static class ConcurrentDdl
{
    /// <summary>unique_violation — a catalog index caught the duplicate insert.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>duplicate_table — also raised for a duplicate index.</summary>
    private const string DuplicateTable = "42P07";

    /// <summary>duplicate_object — extensions, constraints.</summary>
    private const string DuplicateObject = "42710";

    private const string DuplicateSchema = "42P06";

    private const int MaxAttempts = 4;

    internal static bool IsConcurrentCreation(PostgresException ex) =>
        ex.SqlState is UniqueViolation or DuplicateTable or DuplicateObject or DuplicateSchema;

    /// <summary>Execute <paramref name="action"/>, treating a concurrent creator as success.</summary>
    public static async Task RunAsync(
        Func<CancellationToken, Task> action,
        ILogger logger,
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
                logger.LogDebug(
                    "Another coordinator created {What} concurrently ({SqlState}); retrying",
                    what,
                    ex.SqlState);
            }
        }
    }
}
