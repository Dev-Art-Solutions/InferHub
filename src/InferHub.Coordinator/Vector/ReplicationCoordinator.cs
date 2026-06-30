using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Wires the hub-side write/lifecycle events to node replicas. Subscribes to
/// <see cref="LocalVectorStore"/> events and to <see cref="INodeRegistry.Changed"/>, computes
/// target placement from <see cref="VectorStoreOptions.ReplicationFactor"/>, and pushes
/// assignments / ops / drops over the existing SignalR link.
/// The hub-local index stays authoritative; node replicas are derived and disposable.
/// </summary>
public sealed class ReplicationCoordinator : IHostedService, IDisposable
{
    private readonly LocalVectorStore _store;
    private readonly INodeRegistry _registry;
    private readonly ReplicaRegistry _replicas;
    private readonly IHubContext<NodeHub> _hub;
    private readonly IOptions<VectorStoreOptions> _options;
    private readonly ILogger<ReplicationCoordinator> _logger;
    private readonly SemaphoreSlim _placementGate = new(1, 1);

    public ReplicationCoordinator(
        LocalVectorStore store,
        INodeRegistry registry,
        ReplicaRegistry replicas,
        IHubContext<NodeHub> hub,
        IOptions<VectorStoreOptions> options,
        ILogger<ReplicationCoordinator> logger)
    {
        _store = store;
        _registry = registry;
        _replicas = replicas;
        _hub = hub;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.CollectionCreated += OnCollectionCreated;
        _store.CollectionDropped += OnCollectionDropped;
        _store.RecordUpserted += OnRecordUpserted;
        _store.RecordDeleted += OnRecordDeleted;
        // Fleet-change driven re-placement is owned by HealingService (phase 16); the
        // coordinator owns the primitives (seed, fan-out, drop) it calls.
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.CollectionCreated -= OnCollectionCreated;
        _store.CollectionDropped -= OnCollectionDropped;
        _store.RecordUpserted -= OnRecordUpserted;
        _store.RecordDeleted -= OnRecordDeleted;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _placementGate.Dispose();
    }

    /// <summary>The current node connection IDs holding a replica for the collection.</summary>
    public IReadOnlyCollection<string> Holders(string collection) => _replicas.Holders(collection);

    /// <summary>
    /// Apply a reconnecting node's inventory of on-disk replicas. For each (collection,
    /// lastSeq) that exactly matches the hub-local raw store, the node is registered as
    /// a live holder without a re-push. Mismatched or unknown collections are ignored —
    /// the placement loop will heal them.
    /// </summary>
    public void ApplyInventory(string connectionId, IReadOnlyList<InferHub.Shared.Contracts.NodeReplicaInventoryItem>? inventory)
    {
        if (inventory is null || inventory.Count == 0) return;

        foreach (var item in inventory)
        {
            var snapshot = _store.SnapshotForReplica(item.Collection);
            if (snapshot is null) continue;
            if (snapshot.LastSeq != item.LastSeq) continue;

            _replicas.Add(item.Collection, connectionId);
            _logger.LogInformation(
                "Recognised on-disk replica of '{Collection}' (lastSeq={Seq}) on reconnecting {ConnectionId}",
                item.Collection,
                item.LastSeq,
                connectionId);
        }
    }

    /// <summary>
    /// Drive the placement loop directly. Exposed primarily for tests; production code
    /// reacts to <see cref="LocalVectorStore"/> and <see cref="INodeRegistry"/> events.
    /// </summary>
    public Task RecomputeAsync(string collection, CancellationToken cancellationToken = default)
        => PlaceForCollectionAsync(collection, cancellationToken);

    /// <summary>Fan out an op to the current holders. Exposed for tests.</summary>
    public Task ForwardOpAsync(string collection, VectorReplicaOp op)
        => FanOutAsync(collection, op);

    private async void OnCollectionCreated(CollectionInfo info)
    {
        try
        {
            await PlaceForCollectionAsync(info.Name, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seed replicas for new collection '{Collection}'", info.Name);
        }
    }

    private async void OnCollectionDropped(string collection)
    {
        try
        {
            var holders = _replicas.Holders(collection).ToArray();
            _replicas.Clear(collection);
            foreach (var connectionId in holders)
            {
                await SendAsync(connectionId, "DropVectorReplica", collection);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to drop replicas for '{Collection}'", collection);
        }
    }

    private async void OnRecordUpserted(string collection, VectorRecord record)
    {
        var op = new VectorReplicaOp(collection, "upsert", record.Id, record.Vector, record.Payload, record.Metadata, record.SeqNo, record.TimestampUtc);
        await FanOutAsync(collection, op);
    }

    private async void OnRecordDeleted(string collection, string id, long seq, DateTimeOffset ts)
    {
        var op = new VectorReplicaOp(collection, "delete", id, null, null, null, seq, ts);
        await FanOutAsync(collection, op);
    }

    private async Task PlaceForCollectionAsync(string collection, CancellationToken cancellationToken)
    {
        await _placementGate.WaitAsync(cancellationToken);
        try
        {
            var nodes = _registry.Snapshot(DateTimeOffset.UtcNow);
            var ordered = nodes
                .Where(n => !n.Cordoned)
                .OrderBy(n => n.LocalInFlight)
                .ThenBy(n => n.NodeId, StringComparer.Ordinal)
                .Select(n => new NodeCandidate(n.ConnectionId, n.NodeId, n.LocalInFlight))
                .ToArray();

            var target = Math.Max(1, _options.Value.ReplicationFactor);
            var currentHolders = new HashSet<string>(_replicas.Holders(collection), StringComparer.Ordinal);
            // A holder whose node is no longer connected has effectively dropped.
            var connectedIds = ordered.Select(c => c.ConnectionId).ToHashSet(StringComparer.Ordinal);
            var stalePlacements = currentHolders.Where(id => !connectedIds.Contains(id)).ToArray();
            foreach (var stale in stalePlacements)
            {
                if (_replicas.Remove(collection, stale))
                {
                    _logger.LogInformation("Forgot stale replica of '{Collection}' on {ConnectionId}", collection, stale);
                }
            }

            var live = new HashSet<string>(_replicas.Holders(collection), StringComparer.Ordinal);
            var desired = ReplicaPlacement.ComputeTarget(ordered, live, target);
            var desiredSet = new HashSet<string>(desired, StringComparer.Ordinal);

            // Drop replicas no longer in the target set.
            foreach (var holder in live.ToArray())
            {
                if (!desiredSet.Contains(holder))
                {
                    _replicas.Remove(collection, holder);
                    await SendAsync(holder, "DropVectorReplica", collection);
                    _logger.LogInformation("Released replica of '{Collection}' on {ConnectionId}", collection, holder);
                }
            }

            // Add replicas newly required.
            foreach (var holder in desired)
            {
                if (live.Contains(holder)) continue;

                // Register the holder under the collection write-lock so any concurrent
                // upsert is either folded into the snapshot or fans out to this holder
                // afterwards — but never both missed.
                var snapshot = _store.SnapshotAndPin(collection, () => _replicas.Add(collection, holder));
                if (snapshot is null) return;

                var assignment = new VectorReplicaAssignment(
                    snapshot.Collection,
                    snapshot.Dimension,
                    snapshot.Distance,
                    snapshot.Records,
                    snapshot.LastSeq);

                await SendAsync(holder, "AssignVectorReplica", assignment);
                _logger.LogInformation(
                    "Assigned replica of '{Collection}' ({Records} records, lastSeq={Seq}) to {ConnectionId}",
                    collection,
                    snapshot.Records.Count,
                    snapshot.LastSeq,
                    holder);
            }
        }
        finally
        {
            _placementGate.Release();
        }
    }

    private async Task FanOutAsync(string collection, VectorReplicaOp op)
    {
        var holders = _replicas.Holders(collection).ToArray();
        if (holders.Length == 0) return;

        foreach (var connectionId in holders)
        {
            try
            {
                await SendAsync(connectionId, "ApplyVectorOp", op);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to forward {Op} for '{Collection}' to {ConnectionId}", op.Op, collection, connectionId);
            }
        }
    }

    private Task SendAsync(string connectionId, string method, object payload)
    {
        return _hub.Clients.Client(connectionId).SendAsync(method, payload);
    }
}
