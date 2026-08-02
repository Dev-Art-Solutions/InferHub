using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Fleet-level configuration the coordinator holds on behalf of nodes (phase 43). Today that is node
/// profiles; phase 44 adds hub-assigned retrieval beside it.
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    public ProfileOptions Profiles { get; set; } = new();
}

/// <summary>
/// Where node profiles live between coordinator restarts.
/// </summary>
/// <remarks>
/// <para>
/// <b>The third recorded exception to rule 4</b> ("no persisted state except the vector store"), and
/// the reasoning is phase-30 D2's rather than a new one: a profile that evaporates on hub restart is
/// useless for the thing it was asked to do. Rule 4 survives because <b>a lost profile costs the
/// fleet reverting to the operator-configured default on each box</b> — never a wrong answer, and
/// never a capability nobody granted. The node's own config remains the authority for what is
/// <em>possible</em>; a profile is only a preference over it.
/// </para>
/// <para>
/// If a future change ever makes a profile the authority for something a node cannot re-derive
/// locally, that reasoning has stopped being true and the design has drifted — stop.
/// </para>
/// </remarks>
public sealed class ProfileOptions
{
    public const string PersistenceNone = "none";
    public const string PersistenceFile = "file";
    public const string PersistencePostgres = "postgres";

    /// <summary>
    /// <c>none</c> (default — profiles live only as long as the coordinator does), <c>file</c>, or
    /// <c>postgres</c>.
    /// </summary>
    public string Persistence { get; set; } = PersistenceNone;

    /// <summary>
    /// Where the <c>file</c> store writes. <b>The sixth instance of the container permissions
    /// trap</b> (phase-21 D7, phase-30 D3, phase-38 D4, phase-39 D7, phase-41 D5): the default stays
    /// relative so a bare-metal coordinator works out of the box, and the image sets
    /// <c>Fleet__Profiles__DataDirectory=/data/profiles</c> under its existing
    /// <c>chown app:app /data</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "./data/profiles";

    public PostgresProfileOptions Postgres { get; set; } = new();

    public string NormalizedPersistence() => (Persistence ?? string.Empty).Trim() switch
    {
        var value when string.Equals(value, PersistenceFile, StringComparison.OrdinalIgnoreCase) => PersistenceFile,
        var value when string.Equals(value, PersistencePostgres, StringComparison.OrdinalIgnoreCase) => PersistencePostgres,
        _ => PersistenceNone
    };
}

public sealed class PostgresProfileOptions
{
    /// <summary>Env (<c>Fleet__Profiles__Postgres__ConnectionString</c>) or user-secrets. Never appsettings.json.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Becomes a SQL identifier; validated like the usage ledger's.</summary>
    public string Schema { get; set; } = "inferhub";

    public string Table { get; set; } = "node_profiles";

    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class FleetOptionsValidator : IValidateOptions<FleetOptions>
{
    private static readonly System.Text.RegularExpressions.Regex Identifier =
        new("^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ValidateOptionsResult Validate(string? name, FleetOptions options)
    {
        var raw = (options.Profiles.Persistence ?? string.Empty).Trim();

        if (raw.Length > 0
            && !string.Equals(raw, ProfileOptions.PersistenceNone, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(raw, ProfileOptions.PersistenceFile, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(raw, ProfileOptions.PersistencePostgres, StringComparison.OrdinalIgnoreCase))
        {
            // Falling back to `none` on a typo would silently drop every profile on the next
            // restart, which is the failure this key exists to prevent.
            return ValidateOptionsResult.Fail(
                $"Fleet:Profiles:Persistence '{options.Profiles.Persistence}' is not recognised; use 'none', 'file' or 'postgres'.");
        }

        if (options.Profiles.NormalizedPersistence() != ProfileOptions.PersistencePostgres)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Profiles.Postgres.ConnectionString))
        {
            failures.Add("Fleet:Profiles:Postgres:ConnectionString must be set when Fleet:Profiles:Persistence=postgres (via env or user-secrets, never appsettings.json).");
        }

        if (!Identifier.IsMatch(options.Profiles.Postgres.Schema))
        {
            failures.Add($"Fleet:Profiles:Postgres:Schema '{options.Profiles.Postgres.Schema}' is not a valid identifier (^[a-z_][a-z0-9_]*$).");
        }

        if (!Identifier.IsMatch(options.Profiles.Postgres.Table))
        {
            failures.Add($"Fleet:Profiles:Postgres:Table '{options.Profiles.Postgres.Table}' is not a valid identifier (^[a-z_][a-z0-9_]*$).");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
