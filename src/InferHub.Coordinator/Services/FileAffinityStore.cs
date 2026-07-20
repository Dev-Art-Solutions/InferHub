using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// A file-backed <see cref="IAffinityStore"/> (phase 30, <c>Affinity:Persistence=file</c>). It
/// reuses the local vector raw-store discipline rather than inventing a format: an append-only
/// <c>ops.jsonl</c> of record/forget lines, compacted to a <c>snapshot.jsonl</c> every N ops. On
/// startup the snapshot is replayed then the trailing ops, last-write-wins per conversation key.
/// The persisted map is a derived cache of routing hints — never authoritative — so it is flushed
/// but not fsynced on the hot path; a lost tail costs at most a cold model load.
/// </summary>
public sealed class FileAffinityStore : IAffinityStore, IDisposable
{
    private const string SnapshotFileName = "snapshot.jsonl";
    private const string SnapshotTempFileName = "snapshot.jsonl.tmp";
    private const string OpsFileName = "ops.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object writeLock = new();
    private readonly ConcurrentDictionary<string, PersistedAffinity> shadow = new(StringComparer.Ordinal);
    private readonly string directory;
    private readonly int snapshotEveryOps;

    private FileStream? opsStream;
    private long opsSinceSnapshot;

    public FileAffinityStore(IOptions<AffinityOptions> options)
    {
        var value = options.Value;
        directory = string.IsNullOrWhiteSpace(value.DataDirectory) ? "./data/affinity" : value.DataDirectory;
        snapshotEveryOps = Math.Max(1, value.SnapshotEveryOps);
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyCollection<PersistedAffinity> Load()
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

    public void Record(string conversationKey, string nodeId, DateTimeOffset lastUsed)
    {
        if (string.IsNullOrEmpty(conversationKey) || string.IsNullOrEmpty(nodeId))
        {
            return;
        }

        var op = new AffinityOp("record", conversationKey, nodeId, lastUsed);

        lock (writeLock)
        {
            shadow[conversationKey] = new PersistedAffinity(conversationKey, nodeId, lastUsed);
            AppendOp(op);
        }
    }

    public void Forget(string conversationKey)
    {
        if (string.IsNullOrEmpty(conversationKey))
        {
            return;
        }

        var op = new AffinityOp("forget", conversationKey, null, default);

        lock (writeLock)
        {
            shadow.TryRemove(conversationKey, out _);
            AppendOp(op);
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
    private void Apply(AffinityOp op)
    {
        if (string.IsNullOrEmpty(op.Key))
        {
            return;
        }

        if (string.Equals(op.Op, "forget", StringComparison.Ordinal))
        {
            shadow.TryRemove(op.Key, out _);
            return;
        }

        if (string.Equals(op.Op, "record", StringComparison.Ordinal) && !string.IsNullOrEmpty(op.Node))
        {
            shadow[op.Key] = new PersistedAffinity(op.Key, op.Node, op.LastUsed);
        }
    }

    // Caller holds writeLock.
    private void AppendOp(AffinityOp op)
    {
        var line = JsonSerializer.Serialize(op, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        EnsureOpsStream();
        opsStream!.Write(bytes, 0, bytes.Length);
        opsStream.Flush(flushToDisk: false);

        if (++opsSinceSnapshot >= snapshotEveryOps)
        {
            WriteSnapshot();
        }
    }

    // Caller holds writeLock. Rewrites the compacted snapshot from the live shadow and drops the ops log,
    // exactly like RawCollection.WriteSnapshot — a temp file, an atomic move, then truncate the ops.
    private void WriteSnapshot()
    {
        CloseOpsStream(flushToDisk: false);

        var snapshotTmp = Path.Combine(directory, SnapshotTempFileName);
        var snapshotPath = Path.Combine(directory, SnapshotFileName);

        using (var fs = new FileStream(snapshotTmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            foreach (var entry in shadow.Values)
            {
                var op = new AffinityOp("record", entry.ConversationKey, entry.NodeId, entry.LastUsed);
                writer.WriteLine(JsonSerializer.Serialize(op, JsonOptions));
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

    private static IEnumerable<AffinityOp> ReadOps(string path)
    {
        if (!File.Exists(path)) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            AffinityOp? op = null;
            try
            {
                op = JsonSerializer.Deserialize<AffinityOp>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // A torn last line from a crash mid-append is not corruption of a source of truth —
                // it is a lost hint. Skip it and carry on rather than failing startup.
            }

            if (op is not null) yield return op;
        }
    }

    private sealed record AffinityOp(
        [property: JsonPropertyName("op")] string Op,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("node")] string? Node,
        [property: JsonPropertyName("ts")] DateTimeOffset LastUsed);
}
