using System.Text;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InferHub.Coordinator.Auth;

/// <summary>
/// Phase 131. Where the node CA and the record of what it issued live: files on one coordinator, or — when coordinators
/// run as a cluster (phase 32) — the cluster's own Postgres, so a promoted standby verifies the certificates the old active
/// issued (131 D1). Two documents: <see cref="Authority"/> (the CA key and certificate, PEM) and <see cref="Record"/>
/// (certificates and tokens, JSON).
/// </summary>
public interface INodeIdentityStore
{
    public const string Authority = "ca";

    public const string Record = "issued.json";

    /// <summary>For logs: where the documents are.</summary>
    string Describe { get; }

    /// <summary>Whether another coordinator may write the same documents (a cluster), so a cached copy can go stale.</summary>
    bool Shared { get; }

    /// <summary>The document and its version (0 when it does not exist).</summary>
    (string? Body, long Version) Read(string name);

    /// <summary>
    /// Writes only if the document is still at <paramref name="expectedVersion"/> (0: only if it does not exist yet). False
    /// means another coordinator wrote first: read again and redo the change (131 D2).
    /// </summary>
    bool TryWrite(string name, string body, long expectedVersion);
}

/// <summary>The 3.87 layout, unchanged: <c>ca.key</c>, <c>ca.crt</c> and <c>issued.json</c> in <c>Identity:DataDirectory</c>.</summary>
public sealed class FileNodeIdentityStore(string directory) : INodeIdentityStore
{
    private const string KeyFile = "ca.key";
    private const string CertificateFile = "ca.crt";

    public string Describe => Path.GetFullPath(directory);

    public bool Shared => false;

    public (string? Body, long Version) Read(string name)
    {
        if (name == INodeIdentityStore.Authority)
        {
            var key = Path.Combine(directory, KeyFile);
            var certificate = Path.Combine(directory, CertificateFile);
            return File.Exists(key) && File.Exists(certificate)
                ? (File.ReadAllText(key) + "\n" + File.ReadAllText(certificate), 1)
                : (null, 0);
        }

        var path = Path.Combine(directory, name);
        return File.Exists(path) ? (File.ReadAllText(path), 1) : (null, 0);
    }

    public bool TryWrite(string name, string body, long expectedVersion)
    {
        Directory.CreateDirectory(directory);

        if (name == INodeIdentityStore.Authority)
        {
            if (File.Exists(Path.Combine(directory, KeyFile)))
            {
                return false;
            }

            WriteOwnerOnly(Path.Combine(directory, KeyFile), Pem.Block(body, "PRIVATE KEY"));
            WriteOwnerOnly(Path.Combine(directory, CertificateFile), Pem.Block(body, "CERTIFICATE"));
            return true;
        }

        // One coordinator is the only writer of its own files: the version is not needed to keep two apart.
        var path = Path.Combine(directory, name);
        WriteOwnerOnly(path + ".tmp", body);
        File.Move(path + ".tmp", path, overwrite: true);
        return true;
    }

    private static void WriteOwnerOnly(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>
/// Phase 131. The same two documents in the cluster's Postgres (<c>Cluster:ConnectionString</c>, <c>Cluster:Schema</c>), in
/// <c>node_identity(name, body, version)</c>. A write is a conditional insert or update on the version — decided by the
/// database, so two coordinators cannot both win (131 D2). The CA key is in that row: the database holding it is the one the
/// cluster already trusts with its lease, vectors and usage ledger.
/// </summary>
public sealed class PostgresNodeIdentityStore : INodeIdentityStore, IDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly ClusterOptions options;
    private readonly object bootstrapGate = new();
    private readonly INodeIdentityStore? seed;
    private readonly Microsoft.Extensions.Logging.ILogger logger;
    private bool bootstrapped;

    /// <param name="seed">
    /// The files this coordinator used before it joined a cluster (or before 3.91). When the cluster has no authority yet,
    /// the first coordinator to boot moves its CA and record in, so the nodes it certified stay certified (131 D1).
    /// </param>
    public PostgresNodeIdentityStore(IOptions<ClusterOptions> options, INodeIdentityStore? seed = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        this.options = options.Value;
        this.seed = seed;
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        dataSource = new NpgsqlDataSourceBuilder(this.options.ConnectionString).Build();
    }

    private string Table => Quote(options.Schema) + ".\"node_identity\"";

    public string Describe => $"the cluster's Postgres ({options.Schema}.node_identity)";

    public bool Shared => true;

    public (string? Body, long Version) Read(string name)
    {
        Bootstrap();
        using var command = dataSource.CreateCommand($"SELECT body, version FROM {Table} WHERE name = @name");
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", name);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1)) : (null, 0);
    }

    public bool TryWrite(string name, string body, long expectedVersion)
    {
        Bootstrap();
        using var command = dataSource.CreateCommand(expectedVersion == 0
            ? $"INSERT INTO {Table} (name, body, version, updated_at_utc) VALUES (@name, @body, 1, now()) ON CONFLICT (name) DO NOTHING"
            : $"UPDATE {Table} SET body = @body, version = version + 1, updated_at_utc = now() WHERE name = @name AND version = @version");
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("body", body);
        command.Parameters.AddWithValue("version", expectedVersion);
        return command.ExecuteNonQuery() == 1;
    }

    private void Bootstrap()
    {
        if (Volatile.Read(ref bootstrapped))
        {
            return;
        }

        lock (bootstrapGate)
        {
            if (bootstrapped)
            {
                return;
            }

            // 32 D7: two coordinators boot at once as a matter of course; IF NOT EXISTS is not atomic.
            ConcurrentDdl.RunAsync(async ct =>
            {
                await using var command = dataSource.CreateCommand(
                    $"""
                    CREATE SCHEMA IF NOT EXISTS {Quote(options.Schema)};
                    CREATE TABLE IF NOT EXISTS {Table} (
                        name           text PRIMARY KEY,
                        body           text NOT NULL,
                        version        bigint NOT NULL,
                        updated_at_utc timestamptz NOT NULL
                    );
                    """);
                command.CommandTimeout = options.CommandTimeoutSeconds;
                await command.ExecuteNonQueryAsync(ct);
            }, logger, "the node identity table", CancellationToken.None).GetAwaiter().GetResult();

            Seed();
            Volatile.Write(ref bootstrapped, true);
        }
    }

    /// <summary>
    /// The CA and the record go in together or not at all, in one transaction, and only if the cluster has no CA: a record
    /// from one hub beside another hub's CA would be certificates nothing can verify. Whoever wins, the others keep theirs
    /// on disk, untouched.
    /// </summary>
    private void Seed()
    {
        if (seed is null || seed.Read(INodeIdentityStore.Authority) is not { Body: { } authority })
        {
            return;
        }

        var (record, _) = seed.Read(INodeIdentityStore.Record);
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var inserted = Insert(INodeIdentityStore.Authority, authority) == 1;

        if (inserted && record is not null)
        {
            Insert(INodeIdentityStore.Record, record);
        }

        transaction.Commit();

        if (inserted)
        {
            logger.Log(Microsoft.Extensions.Logging.LogLevel.Information,
                "Moved the node CA and its record from {Directory} into {Store}; nodes it certified stay certified", seed.Describe, Describe);
        }

        int Insert(string name, string body)
        {
            using var command = new NpgsqlCommand(
                $"INSERT INTO {Table} (name, body, version, updated_at_utc) VALUES (@name, @body, 1, now()) ON CONFLICT (name) DO NOTHING",
                connection,
                transaction);
            command.CommandTimeout = options.CommandTimeoutSeconds;
            command.Parameters.AddWithValue("name", name);
            command.Parameters.AddWithValue("body", body);
            return command.ExecuteNonQuery();
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public void Dispose() => dataSource.Dispose();
}

internal static class Pem
{
    /// <summary>The first block of a PEM bundle whose label ends with <paramref name="label"/>, with its armour.</summary>
    public static string Block(string bundle, string label)
    {
        var begin = bundle.IndexOf($"{label}-----", StringComparison.Ordinal);
        begin = bundle.LastIndexOf("-----BEGIN", begin, StringComparison.Ordinal);
        var end = bundle.IndexOf($"-----END", begin, StringComparison.Ordinal);
        end = bundle.IndexOf("-----", end + 8, StringComparison.Ordinal) + 5;
        return bundle[begin..end] + "\n";
    }

    public static byte[] Der(string block)
        => Convert.FromBase64String(string.Concat(block.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith("-----", StringComparison.Ordinal))));
}
