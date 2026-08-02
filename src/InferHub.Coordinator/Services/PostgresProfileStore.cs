using System.Text.Json;
using InferHub.Coordinator.Postgres;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Profiles in PostgreSQL (<c>Fleet:Profiles:Persistence=postgres</c>), which is the shape an HA
/// pair needs: two coordinators sharing one fleet configuration rather than two file stores that
/// disagree the first time somebody edits the wrong one.
/// </summary>
/// <remarks>
/// <para>
/// One row per profile, the body as <c>jsonb</c>. The columns that are <em>read</em> — name and
/// revision — are columns; the rest is the contract, and giving each field a column would mean a
/// migration every time phase 44 adds one. <c>Npgsql</c> is already a recorded rule-5 exception
/// (phase 20); nothing new is added here.
/// </para>
/// <para>
/// Bootstrap goes through <see cref="ConcurrentDdl"/> because two coordinators starting at the same
/// instant is the normal case under HA — the phase-32 lesson, applied on the day the table is
/// written rather than after the crash.
/// </para>
/// </remarks>
public sealed class PostgresProfileStore : IProfileStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource dataSource;
    private readonly PostgresProfileOptions options;
    private readonly ILogger<PostgresProfileStore> logger;
    private readonly SemaphoreSlim bootstrapGate = new(1, 1);
    private volatile bool bootstrapped;

    public PostgresProfileStore(IOptions<FleetOptions> fleetOptions, ILogger<PostgresProfileStore> logger)
    {
        options = fleetOptions.Value.Profiles.Postgres;
        this.logger = logger;
        dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
    }

    private string QualifiedTable => Quote(options.Schema) + "." + Quote(options.Table);

    public IReadOnlyCollection<NodeProfile> Load()
    {
        // Synchronous by design — see IProfileStore. This runs once, at construction, before the
        // hub accepts a node.
        return LoadAsync().GetAwaiter().GetResult();
    }

    public void Save(NodeProfile profile) => SaveAsync(profile).GetAwaiter().GetResult();

    public void Delete(string name) => DeleteAsync(name).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        bootstrapGate.Dispose();
        await dataSource.DisposeAsync();
    }

    private async Task<IReadOnlyCollection<NodeProfile>> LoadAsync()
    {
        await EnsureBootstrappedAsync(CancellationToken.None);

        var profiles = new List<NodeProfile>();

        await using var command = dataSource.CreateCommand($"SELECT body FROM {QualifiedTable} ORDER BY name");
        command.CommandTimeout = options.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var body = reader.GetString(0);

            try
            {
                if (JsonSerializer.Deserialize<NodeProfile>(body, JsonOptions) is { } profile)
                {
                    profiles.Add(profile);
                }
            }
            catch (JsonException ex)
            {
                // A row this version cannot read is one profile's worth of fleet configuration, not
                // a reason to refuse to start — those nodes fall back to their own config (D3).
                logger.LogWarning(ex, "Skipping a stored node profile this version could not read");
            }
        }

        return profiles;
    }

    private async Task SaveAsync(NodeProfile profile)
    {
        await EnsureBootstrappedAsync(CancellationToken.None);

        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {QualifiedTable} (name, revision, body, updated_utc)
            VALUES (@name, @revision, @body::jsonb, @updated)
            ON CONFLICT (name) DO UPDATE
                SET revision = EXCLUDED.revision,
                    body = EXCLUDED.body,
                    updated_utc = EXCLUDED.updated_utc
            """);
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", profile.Name);
        command.Parameters.AddWithValue("revision", profile.Revision);
        command.Parameters.AddWithValue("body", JsonSerializer.Serialize(profile, JsonOptions));
        command.Parameters.AddWithValue("updated", DateTimeOffset.UtcNow);

        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteAsync(string name)
    {
        await EnsureBootstrappedAsync(CancellationToken.None);

        await using var command = dataSource.CreateCommand($"DELETE FROM {QualifiedTable} WHERE name = @name");
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", name);

        await command.ExecuteNonQueryAsync();
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

            await ConcurrentDdl.RunAsync(
                async token =>
                {
                    await using var schema = dataSource.CreateCommand(
                        $"CREATE SCHEMA IF NOT EXISTS {Quote(options.Schema)}");
                    await schema.ExecuteNonQueryAsync(token);
                },
                logger,
                $"schema {options.Schema}",
                cancellationToken);

            await ConcurrentDdl.RunAsync(
                async token =>
                {
                    await using var table = dataSource.CreateCommand(
                        $"""
                        CREATE TABLE IF NOT EXISTS {QualifiedTable} (
                            name text PRIMARY KEY,
                            revision bigint NOT NULL,
                            body jsonb NOT NULL,
                            updated_utc timestamptz NOT NULL
                        )
                        """);
                    await table.ExecuteNonQueryAsync(token);
                },
                logger,
                $"table {options.Schema}.{options.Table}",
                cancellationToken);

            bootstrapped = true;
        }
        finally
        {
            bootstrapGate.Release();
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
