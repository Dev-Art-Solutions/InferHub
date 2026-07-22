using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Cluster;

/// <summary>
/// High availability (phase 32). Off by default: with <see cref="Enabled"/> false the coordinator
/// is unconditionally active and every behaviour is byte-identical to v2.13 — there is no lease,
/// no Postgres connection, and no role to think about.
///
/// When on, two (or more) coordinators share <b>one</b> Postgres and exactly one of them holds the
/// lease and serves inference; the others run standby. This does not add a source of truth (rule 4):
/// the lease row is a mutual-exclusion token, not state anyone reads to answer a request. It is
/// deliberately its own connection string, for the same reason the usage ledger is — a deployment
/// may run an HA pair over a Postgres vector store, a Postgres ledger, both, or neither.
/// </summary>
public sealed class ClusterOptions
{
    public const string SectionName = "Cluster";

    public bool Enabled { get; set; }

    /// <summary>
    /// Identifies this coordinator in the lease row and in logs. Defaults to the machine name,
    /// which is what a container gives you for free and is unique in every deployment that has
    /// two hubs.
    /// </summary>
    public string InstanceId { get; set; } = Environment.MachineName;

    /// <summary>Env (<c>Cluster__ConnectionString</c>) or user-secrets. Never appsettings.json.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Becomes a SQL identifier; validated like the vector store's and the ledger's.</summary>
    public string Schema { get; set; } = "inferhub";

    public string Table { get; set; } = "coordinator_lease";

    /// <summary>
    /// One lease per logical deployment. Two unrelated meshes may share a Postgres instance, and
    /// a shared row would have them fighting over a leadership neither asked for.
    /// </summary>
    public string LeaseName { get; set; } = "default";

    /// <summary>
    /// How long a held lease stays valid without a renewal — and therefore the worst-case failover
    /// delay, and the window inside which a partitioned old primary must have fenced itself.
    /// </summary>
    public int LeaseTtlSeconds { get; set; } = 15;

    /// <summary>
    /// How often the active coordinator renews, and how often a standby probes for a free lease.
    /// Must be comfortably under the TTL or a single slow round-trip demotes a healthy primary.
    /// </summary>
    public int RenewIntervalSeconds { get; set; } = 5;

    public int CommandTimeoutSeconds { get; set; } = 10;

    public TimeSpan LeaseTtl => TimeSpan.FromSeconds(LeaseTtlSeconds);

    public TimeSpan RenewInterval => TimeSpan.FromSeconds(RenewIntervalSeconds);
}

public sealed class ClusterOptionsValidator : IValidateOptions<ClusterOptions>
{
    private static readonly System.Text.RegularExpressions.Regex Identifier =
        new("^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ValidateOptionsResult Validate(string? name, ClusterOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add(
                "Cluster:ConnectionString must be set when Cluster:Enabled=true (via env Cluster__ConnectionString " +
                "or user-secrets, never appsettings.json). Both coordinators must point at the same database.");
        }

        if (string.IsNullOrWhiteSpace(options.InstanceId))
        {
            failures.Add("Cluster:InstanceId must be set; it is how the lease row names its holder.");
        }

        if (!Identifier.IsMatch(options.Schema))
        {
            failures.Add($"Cluster:Schema '{options.Schema}' is not a valid identifier (^[a-z_][a-z0-9_]*$).");
        }

        if (!Identifier.IsMatch(options.Table))
        {
            failures.Add($"Cluster:Table '{options.Table}' is not a valid identifier (^[a-z_][a-z0-9_]*$).");
        }

        if (options.LeaseTtlSeconds < 2)
        {
            failures.Add($"Cluster:LeaseTtlSeconds must be >= 2 (got {options.LeaseTtlSeconds}).");
        }

        if (options.RenewIntervalSeconds < 1)
        {
            failures.Add($"Cluster:RenewIntervalSeconds must be >= 1 (got {options.RenewIntervalSeconds}).");
        }

        // The fence is only safe if the holder gets several attempts inside one TTL: at exactly
        // TTL/2 a single dropped packet is enough to demote a perfectly healthy active hub.
        if (options.RenewIntervalSeconds * 3 > options.LeaseTtlSeconds)
        {
            failures.Add(
                $"Cluster:RenewIntervalSeconds ({options.RenewIntervalSeconds}) must be at most a third of " +
                $"Cluster:LeaseTtlSeconds ({options.LeaseTtlSeconds}), so a transient blip does not demote the active coordinator.");
        }

        if (options.CommandTimeoutSeconds < 1)
        {
            failures.Add($"Cluster:CommandTimeoutSeconds must be >= 1 (got {options.CommandTimeoutSeconds}).");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
