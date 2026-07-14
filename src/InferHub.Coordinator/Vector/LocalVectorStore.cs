using System.Collections.Concurrent;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

public sealed class LocalVectorStore : IVectorStore, IDisposable
{
    private static readonly char[] InvalidCollectionChars = Path.GetInvalidFileNameChars();

    private readonly ConcurrentDictionary<string, CollectionEntry> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lifecycleLock = new();
    private readonly string _root;
    private readonly DistanceMetric _defaultDistance;
    private readonly int _snapshotEveryOps;
    private readonly ILogger<LocalVectorStore> _logger;

    public event Action<CollectionInfo>? CollectionCreated;
    public event Action<string>? CollectionDropped;
    public event Action<string, VectorRecord>? RecordUpserted;
    public event Action<string, string, long, DateTimeOffset>? RecordDeleted;

    public LocalVectorStore(IOptions<VectorStoreOptions> options, ILogger<LocalVectorStore> logger)
    {
        var opts = options.Value;
        if (!DistanceMetricExtensions.TryParse(opts.Distance, out _defaultDistance))
        {
            throw new InvalidOperationException($"invalid VectorStore:Distance '{opts.Distance}'");
        }

        _root = Path.GetFullPath(opts.DataDirectory);
        _snapshotEveryOps = Math.Max(1, opts.SnapshotEveryOps);
        _logger = logger;

        Directory.CreateDirectory(_root);
        LoadExisting();
    }

    private void LoadExisting()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            try
            {
                var raw = RawCollection.Open(dir);
                if (!DistanceMetricExtensions.TryParse(raw.Distance, out var metric))
                {
                    _logger.LogWarning("Skipping collection at {Directory}: unknown distance '{Distance}'", dir, raw.Distance);
                    continue;
                }

                var index = new FlatIndex(raw.Dimension, metric);
                var seq = ReplayInto(raw, index);
                _collections[raw.Name] = new CollectionEntry(raw, index, metric, seq);
                _logger.LogInformation("Loaded vector collection '{Collection}' ({Count} records, lastSeq={Seq})", raw.Name, index.Count, seq);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load vector collection at {Directory}", dir);
            }
        }
    }

    private static long ReplayInto(RawCollection raw, FlatIndex index)
    {
        long lastSeq = 0;
        foreach (var op in raw.Replay())
        {
            if (op.SeqNo > lastSeq) lastSeq = op.SeqNo;

            if (op.Op == "upsert" && op.Vector is not null)
            {
                index.Upsert(new VectorRecord(op.Id, op.Vector, op.Payload, op.Metadata, op.SeqNo, op.TimestampUtc));
            }
            else if (op.Op == "delete")
            {
                index.Delete(op.Id);
            }
        }
        return lastSeq;
    }

    public Task<CollectionInfo> CreateCollectionAsync(string name, int dimension, string? distance, CancellationToken cancellationToken = default)
    {
        ValidateCollectionName(name);
        if (dimension < 1) throw new ArgumentOutOfRangeException(nameof(dimension), "dimension must be >= 1");

        var requested = distance is null ? _defaultDistance : ParseOrThrow(distance);

        CollectionInfo info;
        lock (_lifecycleLock)
        {
            if (_collections.ContainsKey(name))
            {
                throw new InvalidOperationException($"collection '{name}' already exists");
            }

            var raw = RawCollection.Create(_root, name, dimension, requested.ToWireString());
            var index = new FlatIndex(dimension, requested);
            var entry = new CollectionEntry(raw, index, requested, lastSeq: 0);
            _collections[name] = entry;
            info = ToInfo(entry);
        }

        CollectionCreated?.Invoke(info);
        return Task.FromResult(info);
    }

    public Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        bool dropped;
        lock (_lifecycleLock)
        {
            if (!_collections.TryRemove(name, out var entry))
            {
                return Task.FromResult(false);
            }

            entry.Raw.Drop();
            dropped = true;
        }

        if (dropped)
        {
            CollectionDropped?.Invoke(name);
        }

        return Task.FromResult(dropped);
    }

    public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var infos = _collections.Values.Select(ToInfo).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        return Task.FromResult<IReadOnlyList<CollectionInfo>>(infos);
    }

    public Task<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_collections.TryGetValue(name, out var entry) ? ToInfo(entry) : null);
    }

    public Task<VectorRecord> UpsertAsync(string collection, VectorUpsert upsert, CancellationToken cancellationToken = default)
    {
        var entry = RequireCollection(collection);
        if (string.IsNullOrWhiteSpace(upsert.Id)) throw new ArgumentException("id is required", nameof(upsert));
        if (upsert.Vector is null || upsert.Vector.Length == 0) throw new ArgumentException("vector is required", nameof(upsert));

        if (upsert.Vector.Length != entry.Raw.Dimension)
        {
            throw new ArgumentException($"vector length {upsert.Vector.Length} does not match collection dimension {entry.Raw.Dimension}", nameof(upsert));
        }

        VectorRecord record;
        lock (entry.WriteLock)
        {
            var seq = ++entry.LastSeq;
            var stored = (float[])upsert.Vector.Clone();
            record = new VectorRecord(upsert.Id, stored, upsert.Payload, upsert.Metadata, seq, DateTimeOffset.UtcNow);
            entry.Raw.AppendUpsert(record);
            entry.Index.Upsert(record);
            entry.OpsSinceSnapshot++;

            MaybeSnapshot(entry);
        }

        RecordUpserted?.Invoke(collection, record);
        return Task.FromResult(record);
    }

    public Task<VectorRecord?> GetAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        var entry = RequireCollection(collection);
        lock (entry.WriteLock)
        {
            return Task.FromResult(entry.Index.Get(id));
        }
    }

    public Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        var entry = RequireCollection(collection);
        long seq;
        DateTimeOffset ts;
        lock (entry.WriteLock)
        {
            if (!entry.Index.Delete(id))
            {
                return Task.FromResult(false);
            }

            seq = ++entry.LastSeq;
            ts = DateTimeOffset.UtcNow;
            entry.Raw.AppendDelete(id, seq, ts);
            entry.OpsSinceSnapshot++;

            MaybeSnapshot(entry);
        }

        RecordDeleted?.Invoke(collection, id, seq, ts);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<VectorMatch>> QueryAsync(string collection, VectorQuery query, CancellationToken cancellationToken = default)
    {
        var entry = RequireCollection(collection);
        if (query.Vector is null || query.Vector.Length == 0) throw new ArgumentException("query vector is required", nameof(query));

        if (query.Vector.Length != entry.Raw.Dimension)
        {
            throw new ArgumentException($"query vector length {query.Vector.Length} does not match collection dimension {entry.Raw.Dimension}", nameof(query));
        }

        lock (entry.WriteLock)
        {
            var matches = entry.Index.Query(query.Vector, query.K, query.Filter);
            return Task.FromResult(matches);
        }
    }

    public Task<IReadOnlyList<VectorEntry>> ScanAsync(
        string collection,
        IReadOnlyDictionary<string, string>? filter,
        int limit,
        string? afterId = null,
        CancellationToken cancellationToken = default)
    {
        var entry = RequireCollection(collection);
        if (limit < 1) return Task.FromResult<IReadOnlyList<VectorEntry>>([]);

        List<VectorEntry> page;
        lock (entry.WriteLock)
        {
            var live = entry.Index.EnumerateLive().AsEnumerable();

            if (filter is { Count: > 0 })
            {
                live = live.Where(r => FlatIndex.MatchesFilter(r.Metadata, filter));
            }

            if (afterId is not null)
            {
                live = live.Where(r => string.CompareOrdinal(r.Id, afterId) > 0);
            }

            page = live
                .OrderBy(r => r.Id, StringComparer.Ordinal)
                .Take(limit)
                .Select(r => new VectorEntry(r.Id, r.Payload, r.Metadata, r.SeqNo, r.TimestampUtc))
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<VectorEntry>>(page);
    }

    public async Task<int> DeleteByFilterAsync(
        string collection,
        IReadOnlyDictionary<string, string> filter,
        CancellationToken cancellationToken = default)
    {
        var entry = RequireCollection(collection);
        if (filter.Count == 0) throw new ArgumentException("filter must not be empty", nameof(filter));

        List<string> ids;
        lock (entry.WriteLock)
        {
            ids = entry.Index.EnumerateLive()
                .Where(r => FlatIndex.MatchesFilter(r.Metadata, filter))
                .Select(r => r.Id)
                .ToList();
        }

        // Deliberately routed through the ordinary per-id delete rather than a bulk path inside
        // the lock: that is what appends to the raw store and raises RecordDeleted, which is how
        // the deletion reaches the node replicas. A "faster" bulk delete here would leave every
        // replica in the fleet still serving the chunks of a document the hub thinks is gone.
        var deleted = 0;
        foreach (var id in ids)
        {
            if (await DeleteAsync(collection, id, cancellationToken)) deleted++;
        }
        return deleted;
    }

    public ReplicaSnapshot? SnapshotForReplica(string collection)
    {
        if (!_collections.TryGetValue(collection, out var entry))
        {
            return null;
        }

        lock (entry.WriteLock)
        {
            var records = entry.Index.EnumerateLive().ToArray();
            return new ReplicaSnapshot(entry.Raw.Name, entry.Raw.Dimension, entry.Metric.ToWireString(), records, entry.LastSeq);
        }
    }

    /// <summary>
    /// Capture a replica snapshot for <paramref name="collection"/> while holding the
    /// collection's write lock. <paramref name="pin"/> runs under the same lock, so any
    /// caller-side registration (e.g. adding a holder to <see cref="ReplicaRegistry"/>)
    /// happens before the lock releases. After <paramref name="pin"/> returns, subsequent
    /// upserts are guaranteed to fan-out to the newly registered holder.
    /// </summary>
    public ReplicaSnapshot? SnapshotAndPin(string collection, Action pin)
    {
        if (!_collections.TryGetValue(collection, out var entry))
        {
            return null;
        }

        lock (entry.WriteLock)
        {
            var records = entry.Index.EnumerateLive().ToArray();
            var snapshot = new ReplicaSnapshot(entry.Raw.Name, entry.Raw.Dimension, entry.Metric.ToWireString(), records, entry.LastSeq);
            pin();
            return snapshot;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _collections.Values)
        {
            entry.Raw.Close();
        }
    }

    private void MaybeSnapshot(CollectionEntry entry)
    {
        if (entry.OpsSinceSnapshot < _snapshotEveryOps) return;

        var live = SnapshotLiveRecords(entry);
        entry.Raw.WriteSnapshot(live, entry.LastSeq);
        entry.OpsSinceSnapshot = 0;
    }

    private static List<VectorRecord> SnapshotLiveRecords(CollectionEntry entry)
    {
        var list = new List<VectorRecord>(entry.Index.Count);
        foreach (var record in entry.Index.EnumerateLive())
        {
            list.Add(record);
        }
        return list;
    }

    private CollectionEntry RequireCollection(string name)
    {
        if (!_collections.TryGetValue(name, out var entry))
        {
            throw new KeyNotFoundException($"collection '{name}' does not exist");
        }
        return entry;
    }

    private static DistanceMetric ParseOrThrow(string distance)
    {
        if (!DistanceMetricExtensions.TryParse(distance, out var metric))
        {
            throw new ArgumentException($"unknown distance '{distance}'; expected cosine, dot, or l2", nameof(distance));
        }
        return metric;
    }

    private static CollectionInfo ToInfo(CollectionEntry entry) => new(
        entry.Raw.Name,
        entry.Raw.Dimension,
        entry.Metric.ToWireString(),
        entry.Index.Count,
        entry.LastSeq);

    private static void ValidateCollectionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("collection name is required", nameof(name));
        }

        if (name.IndexOfAny(InvalidCollectionChars) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains('.'))
        {
            throw new ArgumentException($"collection name '{name}' contains invalid characters", nameof(name));
        }
    }

    private sealed class CollectionEntry(RawCollection raw, FlatIndex index, DistanceMetric metric, long lastSeq)
    {
        public RawCollection Raw { get; } = raw;
        public FlatIndex Index { get; } = index;
        public DistanceMetric Metric { get; } = metric;
        public long LastSeq { get; set; } = lastSeq;
        public long OpsSinceSnapshot { get; set; }
        public object WriteLock { get; } = new();
    }
}

public sealed record ReplicaSnapshot(
    string Collection,
    int Dimension,
    string Distance,
    IReadOnlyList<VectorRecord> Records,
    long LastSeq);
