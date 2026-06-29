using System.Collections.Concurrent;
using InferHub.Node.Configuration;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Vector;

/// <summary>
/// Holds the node's locally-assigned replicas. Each replica is a derived copy of the hub's
/// authoritative raw store; the node persists it under <see cref="VectorReplicaOptions.ReplicaDirectory"/>
/// so a restart does not require a full re-push from the hub.
/// </summary>
public sealed class ReplicaStore : IDisposable
{
    private readonly ConcurrentDictionary<string, ReplicaEntry> _replicas = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly ILogger<ReplicaStore> _logger;

    public ReplicaStore(IOptions<VectorReplicaOptions> options, ILogger<ReplicaStore> logger)
    {
        _root = Path.GetFullPath(options.Value.ReplicaDirectory);
        _logger = logger;
        Directory.CreateDirectory(_root);
        LoadExisting();
    }

    public IReadOnlyCollection<string> Collections => _replicas.Keys.ToArray();

    /// <summary>
    /// Inventory of replicas currently held on disk, used to skip a full re-push on
    /// re-connect when the hub already has the same seqNo.
    /// </summary>
    public IReadOnlyList<InferHub.Shared.Contracts.NodeReplicaInventoryItem> Inventory()
    {
        return _replicas
            .Select(pair => new InferHub.Shared.Contracts.NodeReplicaInventoryItem(pair.Key, pair.Value.LastSeq))
            .ToArray();
    }

    public void Apply(VectorReplicaAssignment assignment)
    {
        if (!DistanceMetricExtensions.TryParse(assignment.Distance, out var metric))
        {
            _logger.LogWarning("Refusing replica assignment for '{Collection}' with unknown distance '{Distance}'", assignment.Collection, assignment.Distance);
            return;
        }

        // Replace any prior copy: an assignment carries the authoritative snapshot, so
        // wiping local state keeps the replica byte-for-byte fresh.
        if (_replicas.TryRemove(assignment.Collection, out var existing))
        {
            existing.Raw.Drop();
        }

        var raw = RawCollection.Create(_root, assignment.Collection, assignment.Dimension, metric.ToWireString());
        var index = new FlatIndex(assignment.Dimension, metric);

        long maxSeq = assignment.LastSeq;
        foreach (var record in assignment.Records)
        {
            raw.AppendUpsert(record);
            index.Upsert(record);
            if (record.SeqNo > maxSeq) maxSeq = record.SeqNo;
        }

        // Fold the assignment directly into a snapshot so future restarts don't replay
        // the bulk-load ops file.
        raw.WriteSnapshot(assignment.Records, maxSeq);

        var entry = new ReplicaEntry(raw, index, metric, maxSeq);
        _replicas[assignment.Collection] = entry;

        _logger.LogInformation(
            "Replica assigned: '{Collection}' ({Records} records, lastSeq={Seq})",
            assignment.Collection,
            assignment.Records.Count,
            maxSeq);
    }

    public void Apply(VectorReplicaOp op)
    {
        if (!_replicas.TryGetValue(op.Collection, out var entry))
        {
            _logger.LogWarning("Ignoring op for unknown replica '{Collection}'", op.Collection);
            return;
        }

        lock (entry.WriteLock)
        {
            if (op.SeqNo <= entry.LastSeq)
            {
                // Stale op (the assignment snapshot was newer); replication is idempotent
                // because the hub is the source of truth.
                return;
            }

            if (op.Op == "upsert")
            {
                if (op.Vector is null) return;
                var record = new VectorRecord(op.Id, op.Vector, op.Payload, op.Metadata, op.SeqNo, op.TimestampUtc);
                entry.Raw.AppendUpsert(record);
                entry.Index.Upsert(record);
            }
            else if (op.Op == "delete")
            {
                if (entry.Index.Delete(op.Id))
                {
                    entry.Raw.AppendDelete(op.Id, op.SeqNo, op.TimestampUtc);
                }
            }

            entry.LastSeq = op.SeqNo;
        }
    }

    public void Drop(string collection)
    {
        if (_replicas.TryRemove(collection, out var entry))
        {
            entry.Raw.Drop();
            _logger.LogInformation("Replica dropped: '{Collection}'", collection);
        }
    }

    public IReadOnlyList<VectorMatch>? Query(VectorQueryRequest request)
    {
        if (!_replicas.TryGetValue(request.Collection, out var entry))
        {
            return null;
        }

        lock (entry.WriteLock)
        {
            return entry.Index.Query(request.Vector, Math.Max(1, request.K), request.Filter);
        }
    }

    public void Dispose()
    {
        foreach (var entry in _replicas.Values)
        {
            entry.Raw.Close();
        }
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
                    _logger.LogWarning("Skipping replica at {Directory}: unknown distance '{Distance}'", dir, raw.Distance);
                    continue;
                }

                var index = new FlatIndex(raw.Dimension, metric);
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

                _replicas[raw.Name] = new ReplicaEntry(raw, index, metric, lastSeq);
                _logger.LogInformation("Loaded replica '{Collection}' ({Count} records, lastSeq={Seq})", raw.Name, index.Count, lastSeq);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load replica at {Directory}", dir);
            }
        }
    }

    private sealed class ReplicaEntry(RawCollection raw, FlatIndex index, DistanceMetric metric, long lastSeq)
    {
        public RawCollection Raw { get; } = raw;
        public FlatIndex Index { get; } = index;
        public DistanceMetric Metric { get; } = metric;
        public long LastSeq { get; set; } = lastSeq;
        public object WriteLock { get; } = new();
    }
}
