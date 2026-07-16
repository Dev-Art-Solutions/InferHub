using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Usage persistence (phase 25, D2). Default is <c>none</c>: counters live in memory and a
/// restart resets them, like everything else on the coordinator — honest, and useless for
/// billing. <c>postgres</c> makes the ledger the <b>second</b> recorded exception to rule 4
/// (after the vector store), with its own connection string on purpose: a deployment may want
/// durable usage without a Postgres vector store, or the reverse. The "one source of truth"
/// rule survives because usage records are append-only facts about work already done, not a
/// second copy of anything.
/// </summary>
public sealed class UsageOptions
{
    public const string SectionName = "Usage";

    public const string PersistenceNone = "none";
    public const string PersistencePostgres = "postgres";

    public string Persistence { get; set; } = PersistenceNone;

    public PostgresUsageOptions Postgres { get; set; } = new();

    public string NormalizedPersistence()
        => string.Equals(Persistence?.Trim(), PersistencePostgres, StringComparison.OrdinalIgnoreCase)
            ? PersistencePostgres
            : PersistenceNone;
}

public sealed class PostgresUsageOptions
{
    /// <summary>Env (<c>Usage__Postgres__ConnectionString</c>) or user-secrets. Never appsettings.json.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Becomes a SQL identifier; validated like the vector store's.</summary>
    public string Schema { get; set; } = "inferhub";

    public string Table { get; set; } = "usage_records";

    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class UsageOptionsValidator : IValidateOptions<UsageOptions>
{
    private static readonly System.Text.RegularExpressions.Regex Identifier =
        new("^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ValidateOptionsResult Validate(string? name, UsageOptions options)
    {
        var normalized = options.Persistence?.Trim() ?? UsageOptions.PersistenceNone;

        if (!string.Equals(normalized, UsageOptions.PersistenceNone, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, UsageOptions.PersistencePostgres, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail(
                $"Usage:Persistence '{options.Persistence}' is not recognised; use 'none' or 'postgres'.");
        }

        if (options.NormalizedPersistence() != UsageOptions.PersistencePostgres)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Postgres.ConnectionString))
        {
            failures.Add("Usage:Postgres:ConnectionString must be set when Usage:Persistence=postgres (via env or user-secrets, never appsettings.json).");
        }

        if (!Identifier.IsMatch(options.Postgres.Schema))
        {
            failures.Add($"Usage:Postgres:Schema '{options.Postgres.Schema}' is not a valid identifier (^[a-z_][a-z0-9_]*$).");
        }

        if (!Identifier.IsMatch(options.Postgres.Table))
        {
            failures.Add($"Usage:Postgres:Table '{options.Postgres.Table}' is not a valid identifier (^[a-z_][a-z0-9_]*$).");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
