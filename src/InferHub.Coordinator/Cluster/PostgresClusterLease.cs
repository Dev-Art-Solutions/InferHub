using InferHub.Coordinator.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Cluster;

/// <summary>
/// The lease, as one row updated by one conditional statement. <c>Npgsql</c> is already a recorded
/// dependency (rule 5) and the durable truth already lives in Postgres under this provider, so
/// leadership costs no new moving part — no ZooKeeper, no etcd, no gossip.
///
/// A PG advisory lock was the obvious alternative and is deliberately not used: it is scoped to a
/// <i>session</i>, so a pooled connection dropping quietly releases leadership with nothing to
/// observe, and it carries no fence and no expiry a partitioned holder can reason about locally.
/// A row with an expiry and an acquisition counter can be read, logged, and reasoned about by both
/// sides — which is the whole job here.
/// </summary>
internal sealed class PostgresClusterLease : IClusterLease, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly ClusterOptions options;
    private readonly ILogger<PostgresClusterLease> logger;
    private readonly SemaphoreSlim bootstrapGate = new(1, 1);
    private volatile bool bootstrapped;

    public PostgresClusterLease(IOptions<ClusterOptions> clusterOptions, ILogger<PostgresClusterLease> logger)
    {
        options = clusterOptions.Value;
        this.logger = logger;

        // Leadership is a handful of tiny statements on a timer; a wide pool would be idle
        // connections held against a database the vector store and ledger also want.
        var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        dataSource = builder.Build();
    }

    private string QualifiedTable => Quote(options.Schema) + "." + Quote(options.Table);

    // Phase 131: who is alive, per lease, by the database's clock.
    private string MembersTable => Quote(options.Schema) + "." + Quote(options.Table + "_members");

    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        await EnsureBootstrappedAsync(cancellationToken);
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {MembersTable} (lease_name, instance, seen_at_utc) VALUES (@name, @instance, now())
            ON CONFLICT (lease_name, instance) DO UPDATE SET seen_at_utc = now()
            """);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);
        command.Parameters.AddWithValue("instance", options.InstanceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AdvertiseAsync(string url, string models, CancellationToken cancellationToken)
    {
        await EnsureBootstrappedAsync(cancellationToken);
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {MembersTable} (lease_name, instance, seen_at_utc, url, models) VALUES (@name, @instance, now(), @url, @models)
            ON CONFLICT (lease_name, instance) DO UPDATE SET seen_at_utc = now(), url = EXCLUDED.url, models = EXCLUDED.models
            """);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);
        command.Parameters.AddWithValue("instance", options.InstanceId);
        command.Parameters.AddWithValue("url", url);
        command.Parameters.AddWithValue("models", models);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ClusterMemberRow>> MembersAsync(CancellationToken cancellationToken)
    {
        await EnsureBootstrappedAsync(cancellationToken);
        await using var command = dataSource.CreateCommand(
            $"SELECT instance, url, EXTRACT(EPOCH FROM (now() - seen_at_utc))::float8, models FROM {MembersTable} WHERE lease_name = @name");
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ClusterMemberRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ClusterMemberRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(2))),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    public async Task<TimeSpan?> SinceSeenAsync(string instance, CancellationToken cancellationToken)
    {
        await EnsureBootstrappedAsync(cancellationToken);
        await using var command = dataSource.CreateCommand(
            $"SELECT EXTRACT(EPOCH FROM (now() - seen_at_utc))::float8 FROM {MembersTable} WHERE lease_name = @name AND instance = @instance");
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);
        command.Parameters.AddWithValue("instance", instance);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is double seconds ? TimeSpan.FromSeconds(Math.Max(0, seconds)) : null;
    }

    public async Task<LeaseResult> TryAcquireOrRenewAsync(CancellationToken cancellationToken)
    {
        await EnsureBootstrappedAsync(cancellationToken);

        // One statement, one round-trip, decided entirely by the database clock — there is no
        // read-then-write window for two coordinators to both walk through. The DO UPDATE fires
        // only when the row is already ours or the previous holder's claim has lapsed; otherwise
        // nothing is updated and nothing is returned, which is a clean "someone else has it".
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {QualifiedTable} AS lease
                (lease_name, holder, fence, acquired_at_utc, renewed_at_utc, expires_at_utc)
            VALUES (@name, @holder, 1, now(), now(), now() + make_interval(secs => @ttl))
            ON CONFLICT (lease_name) DO UPDATE SET
                holder          = @holder,
                fence           = CASE WHEN lease.holder = @holder THEN lease.fence ELSE lease.fence + 1 END,
                acquired_at_utc = CASE WHEN lease.holder = @holder THEN lease.acquired_at_utc ELSE now() END,
                renewed_at_utc  = now(),
                expires_at_utc  = now() + make_interval(secs => @ttl)
            WHERE lease.holder = @holder OR lease.expires_at_utc <= now()
            RETURNING fence, expires_at_utc
            """);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);
        command.Parameters.AddWithValue("holder", options.InstanceId);
        command.Parameters.AddWithValue("ttl", (double)options.LeaseTtlSeconds);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                return new LeaseResult(
                    Held: true,
                    Fence: reader.GetInt64(0),
                    Holder: options.InstanceId,
                    ExpiresAtUtc: reader.GetFieldValue<DateTimeOffset>(1));
            }
        }

        return await ReadHolderAsync(cancellationToken);
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (!bootstrapped)
        {
            return;
        }

        // Conditional on still being ours: releasing a lease someone else has already taken would
        // hand the mesh a leaderless window we then have to wait a whole TTL to climb out of.
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE {QualifiedTable}
            SET expires_at_utc = now()
            WHERE lease_name = @name AND holder = @holder
            """);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);
        command.Parameters.AddWithValue("holder", options.InstanceId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<LeaseResult> ReadHolderAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT holder, fence, expires_at_utc FROM {QualifiedTable} WHERE lease_name = @name");
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", options.LeaseName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LeaseResult(false, 0, null, null);
        }

        return new LeaseResult(
            Held: false,
            Fence: reader.GetInt64(1),
            Holder: reader.GetString(0),
            ExpiresAtUtc: reader.GetFieldValue<DateTimeOffset>(2));
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

            await CreateSchemaAndTableAsync(cancellationToken);
            bootstrapped = true;
            logger.LogInformation("Coordinator lease table {Table} is ready", QualifiedTable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Failed to prepare the PostgreSQL coordinator lease. Check Cluster:ConnectionString " +
                $"(set it via env Cluster__ConnectionString or user-secrets). Underlying error: {ex.Message}", ex);
        }
        finally
        {
            bootstrapGate.Release();
        }
    }

    /// <summary>
    /// Create the schema and table, tolerating two coordinators booting at the same moment — the
    /// case this phase makes routine. See <see cref="ConcurrentDdl"/> for why `IF NOT EXISTS` is
    /// not enough on its own.
    /// </summary>
    private Task CreateSchemaAndTableAsync(CancellationToken cancellationToken) =>
        ConcurrentDdl.RunAsync(async ct =>
        {
            await using var command = dataSource.CreateCommand(
                $"""
                CREATE SCHEMA IF NOT EXISTS {Quote(options.Schema)};
                CREATE TABLE IF NOT EXISTS {QualifiedTable} (
                    lease_name      text PRIMARY KEY,
                    holder          text NOT NULL,
                    fence           bigint NOT NULL,
                    acquired_at_utc timestamptz NOT NULL,
                    renewed_at_utc  timestamptz NOT NULL,
                    expires_at_utc  timestamptz NOT NULL
                );
                CREATE TABLE IF NOT EXISTS {MembersTable} (
                    lease_name  text NOT NULL,
                    instance    text NOT NULL,
                    seen_at_utc timestamptz NOT NULL,
                    PRIMARY KEY (lease_name, instance)
                );
                -- Phase 132: where an active-active member is reached, and what its nodes serve.
                ALTER TABLE {MembersTable} ADD COLUMN IF NOT EXISTS url text;
                ALTER TABLE {MembersTable} ADD COLUMN IF NOT EXISTS models text;
                """);
            command.CommandTimeout = options.CommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync(ct);
        }, logger, "the coordinator lease table", cancellationToken);

    // Schema and table names are validated as ^[a-z_][a-z0-9_]*$ at startup; quoting is a second
    // belt on the same trousers, exactly as in the vector store and the usage ledger.
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
