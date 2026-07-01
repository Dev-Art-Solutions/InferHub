using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Watches the fleet and the vector store, and keeps every collection at
/// <see cref="VectorStoreOptions.ReplicationFactor"/> live node replicas. When a holder
/// drops or a new node joins below target, it triggers a debounced re-push from the
/// raw store. When the last node holder dies the hub-local index keeps answering reads
/// and the next eligible node is seeded.
/// </summary>
public sealed class HealingService : IHostedService, IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DefaultIdleSweep = TimeSpan.FromSeconds(15);

    private readonly LocalVectorStore _store;
    private readonly INodeRegistry _registry;
    private readonly ReplicaRegistry _replicas;
    private readonly ReplicationCoordinator _replication;
    private readonly IOptions<VectorStoreOptions> _options;
    private readonly Metrics _metrics;
    private readonly VectorEvents? _events;
    private readonly ILogger<HealingService> _logger;

    private readonly TimeSpan _debounce;
    private readonly TimeSpan _idleSweep;

    private readonly object _gate = new();
    private CancellationTokenSource? _stopCts;
    private Task? _loop;
    private DateTimeOffset _nextRunAt = DateTimeOffset.MaxValue;
    private bool _pending;
    private readonly SemaphoreSlim _healLock = new(1, 1);

    public HealingService(
        LocalVectorStore store,
        INodeRegistry registry,
        ReplicaRegistry replicas,
        ReplicationCoordinator replication,
        IOptions<VectorStoreOptions> options,
        Metrics metrics,
        ILogger<HealingService> logger,
        VectorEvents? events = null)
    {
        _store = store;
        _registry = registry;
        _replicas = replicas;
        _replication = replication;
        _options = options;
        _metrics = metrics;
        _events = events;
        _logger = logger;

        _debounce = TimeSpan.FromMilliseconds(Math.Max(50, options.Value.Healing.DebounceMilliseconds));
        _idleSweep = TimeSpan.FromSeconds(Math.Max(1, options.Value.Healing.IdleSweepSeconds));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _registry.Changed += OnFleetChanged;
        _store.CollectionCreated += OnCollectionCreated;
        _loop = Task.Run(() => RunAsync(_stopCts.Token));
        // Seed an immediate sweep so existing fleet state is reconciled at startup.
        ScheduleHeal(immediate: true);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.Changed -= OnFleetChanged;
        _store.CollectionCreated -= OnCollectionCreated;

        if (_stopCts is not null)
        {
            try { _stopCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _stopCts?.Dispose();
        _healLock.Dispose();
    }

    /// <summary>
    /// Force a heal pass. Exposed for the admin rebuild endpoint and tests.
    /// </summary>
    public Task HealNowAsync(CancellationToken cancellationToken = default) => RunHealPassAsync(cancellationToken);

    /// <summary>
    /// Force a heal of a single collection. Used by the admin rebuild endpoint.
    /// </summary>
    public async Task RebuildAsync(string collection, CancellationToken cancellationToken = default)
    {
        var info = await _store.GetCollectionAsync(collection, cancellationToken);
        if (info is null) throw new KeyNotFoundException($"collection '{collection}' does not exist");

        _metrics.RecordVectorRebuildFromRaw();
        _events?.Publish("vector.heal.started", collection, new Dictionary<string, object?>
        {
            ["reason"] = "rebuild"
        });
        _logger.LogInformation("Rebuilding replicas for '{Collection}' on demand", collection);
        var before = _replicas.Holders(collection).Count;
        await _replication.RecomputeAsync(collection, cancellationToken);
        var after = _replicas.Holders(collection).Count;
        UpdateUnderReplicatedGauge();
        _events?.Publish("vector.heal.completed", collection, new Dictionary<string, object?>
        {
            ["reason"] = "rebuild",
            ["before"] = before,
            ["after"] = after
        });
    }

    private void OnFleetChanged() => ScheduleHeal();

    private void OnCollectionCreated(InferHub.Shared.Vector.CollectionInfo _) => ScheduleHeal();

    private void ScheduleHeal(bool immediate = false)
    {
        lock (_gate)
        {
            _pending = true;
            var fireAt = immediate ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow + _debounce;
            if (fireAt < _nextRunAt) _nextRunAt = fireAt;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Wake up either when a pending heal is due, or periodically to refresh
                // the under-replication gauge.
                await Task.Delay(TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);

                bool shouldRun;
                lock (_gate)
                {
                    shouldRun = _pending && DateTimeOffset.UtcNow >= _nextRunAt;
                    if (shouldRun)
                    {
                        _pending = false;
                        _nextRunAt = DateTimeOffset.MaxValue;
                    }
                }

                if (shouldRun)
                {
                    try
                    {
                        await RunHealPassAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Vector heal pass failed");
                    }
                }
                else
                {
                    // Periodic gauge refresh keeps "under-replicated" honest even when
                    // nothing happens in the fleet for a while.
                    if (DateTimeOffset.UtcNow - _lastGaugeAt > _idleSweep)
                    {
                        UpdateUnderReplicatedGauge();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private DateTimeOffset _lastGaugeAt = DateTimeOffset.MinValue;

    private async Task RunHealPassAsync(CancellationToken cancellationToken)
    {
        if (!await _healLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // A heal is already in flight — its outcome will satisfy whatever caused this
            // request, so we don't queue another behind it.
            return;
        }

        try
        {
            var collections = await _store.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            var target = Math.Max(1, _options.Value.ReplicationFactor);
            var nodes = _registry.Snapshot(DateTimeOffset.UtcNow);
            var connectedConnectionIds = new HashSet<string>(nodes.Select(n => n.ConnectionId), StringComparer.Ordinal);
            var eligibleCount = nodes.Count(n => !n.Cordoned);

            foreach (var info in collections)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var liveHolders = _replicas.Holders(info.Name)
                    .Where(connectedConnectionIds.Contains)
                    .ToArray();

                var liveCount = liveHolders.Length;
                var desired = Math.Min(target, eligibleCount);

                if (liveCount < desired)
                {
                    _logger.LogInformation(
                        "Healing '{Collection}': {Live} of {Target} replicas live (eligible nodes={Eligible})",
                        info.Name,
                        liveCount,
                        desired,
                        eligibleCount);

                    var reason = liveCount == 0 && eligibleCount > 0 ? "last-holder-down" : "under-target";
                    if (liveCount == 0 && eligibleCount > 0)
                    {
                        // Last node holder dropped (or never assigned): the hub-local
                        // index keeps answering reads while we seed a survivor.
                        _logger.LogWarning(
                            "Last node holder of '{Collection}' lost; rebuilding from raw store",
                            info.Name);
                        _metrics.RecordVectorRebuildFromRaw();
                    }

                    _events?.Publish("vector.heal.started", info.Name, new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["live"] = liveCount,
                        ["desired"] = desired
                    });

                    var beforeHolders = _replicas.Holders(info.Name).Count;
                    await _replication.RecomputeAsync(info.Name, cancellationToken).ConfigureAwait(false);
                    var afterHolders = _replicas.Holders(info.Name).Count;

                    var added = Math.Max(0, afterHolders - beforeHolders);
                    for (var i = 0; i < added; i++)
                    {
                        _metrics.RecordVectorReplicaHealed();
                    }

                    _events?.Publish("vector.heal.completed", info.Name, new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["before"] = beforeHolders,
                        ["after"] = afterHolders
                    });
                }
                else if (liveCount > target)
                {
                    // Over-target (e.g. a previously-seeded node came back) — drop the extras.
                    await _replication.RecomputeAsync(info.Name, cancellationToken).ConfigureAwait(false);
                }
            }

            UpdateUnderReplicatedGauge();
        }
        finally
        {
            _healLock.Release();
        }
    }

    private void UpdateUnderReplicatedGauge()
    {
        try
        {
            var target = Math.Max(1, _options.Value.ReplicationFactor);
            var nodes = _registry.Snapshot(DateTimeOffset.UtcNow);
            var connectedConnectionIds = new HashSet<string>(nodes.Select(n => n.ConnectionId), StringComparer.Ordinal);
            var eligibleCount = nodes.Count(n => !n.Cordoned);
            var desired = Math.Min(target, eligibleCount);

            long under = 0;
            foreach (var pair in _replicas.Snapshot())
            {
                var live = pair.Value.Count(connectedConnectionIds.Contains);
                if (live < desired) under++;
            }

            // Also flag collections that exist on the store but have no placement yet.
            // We sample the store on the slow path; an exact count isn't worth a write lock.
            _metrics.SetVectorUnderReplicated(under);
            _lastGaugeAt = DateTimeOffset.UtcNow;
        }
        catch
        {
            // Best-effort gauge — never let observability break the loop.
        }
    }
}
