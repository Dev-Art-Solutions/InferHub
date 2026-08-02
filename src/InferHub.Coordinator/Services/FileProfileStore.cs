using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// A file-backed <see cref="IProfileStore"/> (<c>Fleet:Profiles:Persistence=file</c>). It reuses
/// <see cref="FileAffinityStore"/>'s discipline rather than inventing a second on-disk format: an
/// append-only <c>ops.jsonl</c> of put/delete lines, compacted into <c>snapshot.jsonl</c> every N
/// ops, replayed snapshot-then-ops on startup with last-write-wins per profile name.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the affinity store, a profile is not a hint — losing one changes what a fleet is
/// configured to do. So this one <b>does</b> flush to disk on every write. The volume is an
/// operator typing into an admin API, not a request path, so the cost is not a cost.
/// </para>
/// <para>
/// A torn last line from a crash mid-append is still skipped rather than fatal: an unreadable tail
/// costs the fleet the most recent edit and reverts those nodes to their own configuration, which is
/// exactly what D3 says a lost profile is allowed to cost.
/// </para>
/// </remarks>
public sealed class FileProfileStore : IProfileStore, IDisposable
{
    private const string SnapshotFileName = "snapshot.jsonl";
    private const string SnapshotTempFileName = "snapshot.jsonl.tmp";
    private const string OpsFileName = "ops.jsonl";
    private const int SnapshotEveryOps = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object writeLock = new();
    private readonly ConcurrentDictionary<string, NodeProfile> shadow = new(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;

    private FileStream? opsStream;
    private long opsSinceSnapshot;

    public FileProfileStore(IOptions<FleetOptions> options)
    {
        var value = options.Value.Profiles;
        directory = string.IsNullOrWhiteSpace(value.DataDirectory) ? "./data/profiles" : value.DataDirectory;
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyCollection<NodeProfile> Load()
    {
        lock (writeLock)
        {
            shadow.Clear();

            foreach (var op in ReadOps(Path.Combine(directory, SnapshotFileName)))
            {
                Apply(op);
            }

            foreach (var op in ReadOps(Path.Combine(directory, OpsFileName)))
            {
                Apply(op);
            }

            opsSinceSnapshot = 0;
            return shadow.Values.ToArray();
        }
    }

    public void Save(NodeProfile profile)
    {
        lock (writeLock)
        {
            shadow[profile.Name] = profile;
            AppendOp(new ProfileOp("put", profile.Name, profile));
        }
    }

    public void Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (writeLock)
        {
            shadow.TryRemove(name, out _);
            AppendOp(new ProfileOp("delete", name, null));
        }
    }

    public void Dispose()
    {
        lock (writeLock)
        {
            CloseOpsStream(flushToDisk: true);
        }
    }

    // Caller holds writeLock.
    private void Apply(ProfileOp op)
    {
        if (string.IsNullOrWhiteSpace(op.Name))
        {
            return;
        }

        if (string.Equals(op.Op, "delete", StringComparison.Ordinal))
        {
            shadow.TryRemove(op.Name, out _);
            return;
        }

        if (string.Equals(op.Op, "put", StringComparison.Ordinal) && op.Profile is not null)
        {
            shadow[op.Name] = op.Profile;
        }
    }

    // Caller holds writeLock.
    private void AppendOp(ProfileOp op)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(op, JsonOptions) + "\n");

        EnsureOpsStream();
        opsStream!.Write(bytes, 0, bytes.Length);
        opsStream.Flush(flushToDisk: true);

        if (++opsSinceSnapshot >= SnapshotEveryOps)
        {
            WriteSnapshot();
        }
    }

    // Caller holds writeLock. A temp file, an atomic move, then truncate the ops — RawCollection's
    // shape, third use.
    private void WriteSnapshot()
    {
        CloseOpsStream(flushToDisk: false);

        var snapshotTmp = Path.Combine(directory, SnapshotTempFileName);
        var snapshotPath = Path.Combine(directory, SnapshotFileName);

        using (var fs = new FileStream(snapshotTmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            foreach (var profile in shadow.Values)
            {
                writer.WriteLine(JsonSerializer.Serialize(new ProfileOp("put", profile.Name, profile), JsonOptions));
            }

            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        File.Move(snapshotTmp, snapshotPath);

        var opsPath = Path.Combine(directory, OpsFileName);
        if (File.Exists(opsPath)) File.Delete(opsPath);

        opsSinceSnapshot = 0;
    }

    private void EnsureOpsStream()
    {
        opsStream ??= new FileStream(Path.Combine(directory, OpsFileName), FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private void CloseOpsStream(bool flushToDisk)
    {
        if (opsStream is null) return;
        opsStream.Flush(flushToDisk);
        opsStream.Dispose();
        opsStream = null;
    }

    private static IEnumerable<ProfileOp> ReadOps(string path)
    {
        if (!File.Exists(path)) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            ProfileOp? op = null;

            try
            {
                op = JsonSerializer.Deserialize<ProfileOp>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // See the remarks: a torn tail costs the newest edit, never a startup.
            }

            if (op is not null) yield return op;
        }
    }

    private sealed record ProfileOp(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("profile")] NodeProfile? Profile);
}
