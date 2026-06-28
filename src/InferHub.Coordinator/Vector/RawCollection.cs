using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

internal sealed class RawCollection
{
    private const string MetaFileName = "meta.json";
    private const string SnapshotFileName = "snapshot.jsonl";
    private const string SnapshotTempFileName = "snapshot.jsonl.tmp";
    private const string SnapshotSeqFileName = "snapshot.seq";
    private const string SnapshotSeqTempFileName = "snapshot.seq.tmp";
    private const string OpsFileName = "ops.jsonl";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _writeLock = new();
    private readonly string _directory;
    private FileStream? _opsStream;

    private RawCollection(string directory, string name, int dimension, string distance)
    {
        _directory = directory;
        Name = name;
        Dimension = dimension;
        Distance = distance;
    }

    public string Name { get; }

    public int Dimension { get; }

    public string Distance { get; }

    public static RawCollection Create(string root, string name, int dimension, string distance)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);

        var meta = new CollectionMeta(name, dimension, distance);
        File.WriteAllText(Path.Combine(directory, MetaFileName), JsonSerializer.Serialize(meta, JsonOptions));

        return new RawCollection(directory, name, dimension, distance);
    }

    public static RawCollection Open(string directory)
    {
        var metaPath = Path.Combine(directory, MetaFileName);
        if (!File.Exists(metaPath))
        {
            throw new InvalidOperationException($"collection at '{directory}' has no {MetaFileName}");
        }

        var meta = JsonSerializer.Deserialize<CollectionMeta>(File.ReadAllText(metaPath), JsonOptions)
            ?? throw new InvalidOperationException($"failed to parse {metaPath}");

        var name = string.IsNullOrWhiteSpace(meta.Name) ? Path.GetFileName(directory) : meta.Name;
        return new RawCollection(directory, name, meta.Dimension, meta.Distance);
    }

    public void Drop()
    {
        lock (_writeLock)
        {
            CloseOpsStream();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    public void AppendUpsert(VectorRecord record)
    {
        var op = new RawOp("upsert", record.Id, record.Vector, record.Payload, record.Metadata, record.SeqNo, record.TimestampUtc);
        AppendOp(op);
    }

    public void AppendDelete(string id, long seqNo, DateTimeOffset timestamp)
    {
        var op = new RawOp("delete", id, null, null, null, seqNo, timestamp);
        AppendOp(op);
    }

    public IEnumerable<RawOp> Replay()
    {
        long snapshotSeq = 0;
        var snapshotPath = Path.Combine(_directory, SnapshotFileName);
        var snapshotSeqPath = Path.Combine(_directory, SnapshotSeqFileName);

        if (File.Exists(snapshotPath))
        {
            if (File.Exists(snapshotSeqPath))
            {
                _ = long.TryParse(File.ReadAllText(snapshotSeqPath).Trim(), out snapshotSeq);
            }

            foreach (var op in ReadOps(snapshotPath))
            {
                yield return op;
            }
        }

        var opsPath = Path.Combine(_directory, OpsFileName);
        if (File.Exists(opsPath))
        {
            foreach (var op in ReadOps(opsPath))
            {
                // Ops file may still contain records that were folded into the snapshot
                // if a crash happened between snapshot rename and ops truncate.
                if (op.SeqNo > snapshotSeq)
                {
                    yield return op;
                }
            }
        }
    }

    public long EstimateOpsSinceSnapshot()
    {
        var opsPath = Path.Combine(_directory, OpsFileName);
        if (!File.Exists(opsPath)) return 0;

        long count = 0;
        using var stream = new FileStream(opsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line)) count++;
        }
        return count;
    }

    public void WriteSnapshot(IReadOnlyList<VectorRecord> liveRecords, long snapshotSeq)
    {
        lock (_writeLock)
        {
            CloseOpsStream();

            var snapshotTmp = Path.Combine(_directory, SnapshotTempFileName);
            var seqTmp = Path.Combine(_directory, SnapshotSeqTempFileName);

            using (var fs = new FileStream(snapshotTmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                foreach (var record in liveRecords)
                {
                    var op = new RawOp("upsert", record.Id, record.Vector, record.Payload, record.Metadata, record.SeqNo, record.TimestampUtc);
                    writer.WriteLine(JsonSerializer.Serialize(op, JsonOptions));
                }
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            File.WriteAllText(seqTmp, snapshotSeq.ToString());

            var snapshotPath = Path.Combine(_directory, SnapshotFileName);
            var seqPath = Path.Combine(_directory, SnapshotSeqFileName);

            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            if (File.Exists(seqPath)) File.Delete(seqPath);
            File.Move(snapshotTmp, snapshotPath);
            File.Move(seqTmp, seqPath);

            var opsPath = Path.Combine(_directory, OpsFileName);
            if (File.Exists(opsPath)) File.Delete(opsPath);
        }
    }

    public void Close()
    {
        lock (_writeLock)
        {
            CloseOpsStream();
        }
    }

    private void AppendOp(RawOp op)
    {
        var line = JsonSerializer.Serialize(op, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        lock (_writeLock)
        {
            EnsureOpsStream();
            _opsStream!.Write(bytes, 0, bytes.Length);
            _opsStream.Flush(flushToDisk: false);
        }
    }

    private void EnsureOpsStream()
    {
        if (_opsStream is not null) return;
        var path = Path.Combine(_directory, OpsFileName);
        _opsStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private void CloseOpsStream()
    {
        if (_opsStream is null) return;
        _opsStream.Flush(flushToDisk: true);
        _opsStream.Dispose();
        _opsStream = null;
    }

    private static IEnumerable<RawOp> ReadOps(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var op = JsonSerializer.Deserialize<RawOp>(line, JsonOptions);
            if (op is not null) yield return op;
        }
    }

    private sealed record CollectionMeta(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("dimension")] int Dimension,
        [property: JsonPropertyName("distance")] string Distance);
}

internal sealed record RawOp(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[]? Vector,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("seqNo")] long SeqNo,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);
