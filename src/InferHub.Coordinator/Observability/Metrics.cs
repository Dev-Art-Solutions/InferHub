using System.Collections.Concurrent;

namespace InferHub.Coordinator.Observability;

public sealed class Metrics
{
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    private long requestsTotal;
    private long requestsInFlight;
    private long requestsCompleted;
    private long requestsFailed;
    private long failoversAttempted;
    private long failoversSucceeded;
    private long nodesEvicted;
    private long openAiRequestsTotal;
    private long fallbackDispatched;
    private long vectorReplicasHealed;
    private long vectorRebuildsFromRaw;
    private long vectorUnderReplicated;

    private volatile string? lastFallbackModel;
    private long lastFallbackAtTicks;

    private readonly ConcurrentDictionary<string, NodeCounter> perNode = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VectorCollectionCounter> perCollection = new(StringComparer.OrdinalIgnoreCase);

    public void RecordRequestStart(string nodeId)
    {
        Interlocked.Increment(ref requestsTotal);
        Interlocked.Increment(ref requestsInFlight);

        var counter = perNode.GetOrAdd(nodeId, _ => new NodeCounter());
        Interlocked.Increment(ref counter.Total);
        Interlocked.Increment(ref counter.InFlight);
    }

    public void RecordRequestComplete(string nodeId)
    {
        Interlocked.Increment(ref requestsCompleted);
        DecrementInFlight();

        if (perNode.TryGetValue(nodeId, out var counter))
        {
            Interlocked.Increment(ref counter.Completed);
            DecrementInFlight(counter);
        }
    }

    public void RecordRequestFail(string nodeId)
    {
        Interlocked.Increment(ref requestsFailed);
        DecrementInFlight();

        if (perNode.TryGetValue(nodeId, out var counter))
        {
            Interlocked.Increment(ref counter.Failed);
            DecrementInFlight(counter);
        }
    }

    public void RecordFailoverAttempted() => Interlocked.Increment(ref failoversAttempted);

    public void RecordFailoverSucceeded() => Interlocked.Increment(ref failoversSucceeded);

    public void RecordNodeEvicted() => Interlocked.Increment(ref nodesEvicted);

    // How much of the traffic arrives over the OpenAI dialect. One number — the per-node and
    // per-collection trees already exist and a third would be a metrics system, not a metric.
    public void RecordOpenAiRequest() => Interlocked.Increment(ref openAiRequestsTotal);

    /// <summary>
    /// A request left the fleet. The model name is recorded; the prompt and the answer are not,
    /// and never will be (rule 7). This counter is the thing that makes cloud burst visible
    /// rather than quiet, so it is surfaced on /api/status and the status page.
    /// </summary>
    public void RecordFallbackDispatched(string model)
    {
        Interlocked.Increment(ref fallbackDispatched);
        lastFallbackModel = model;
        Interlocked.Exchange(ref lastFallbackAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordVectorReplicaHealed() => Interlocked.Increment(ref vectorReplicasHealed);

    public void RecordVectorRebuildFromRaw() => Interlocked.Increment(ref vectorRebuildsFromRaw);

    public void SetVectorUnderReplicated(long count) => Interlocked.Exchange(ref vectorUnderReplicated, Math.Max(0, count));

    public void RecordVectorQuery(string collection, TimeSpan elapsed)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Increment(ref counter.Queries);
        var micros = (long)Math.Round(elapsed.TotalMilliseconds * 1000.0);
        Interlocked.Add(ref counter.QueryLatencyMicrosTotal, Math.Max(0, micros));
    }

    /// <summary>
    /// One document finished ingesting. Like everything else in <see cref="Metrics"/> these are
    /// **since-start** counters, not a census — they are named so, and the authoritative document
    /// count is whatever <c>GET /api/collections/{c}/documents</c> reads back out of the store.
    /// </summary>
    public void RecordDocumentIngested(string collection, string embeddingModel)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Increment(ref counter.DocumentsIngested);
        counter.LastEmbeddingModel = embeddingModel;
        Interlocked.Exchange(ref counter.LastIngestAtTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void RecordChunksEmbedded(string collection, int count)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Add(ref counter.ChunksEmbedded, Math.Max(0, count));
    }

    public void RecordIngestionFailure(string collection)
    {
        var counter = perCollection.GetOrAdd(collection, _ => new VectorCollectionCounter());
        Interlocked.Increment(ref counter.IngestionFailures);
    }

    public VectorCollectionMetricsSnapshot GetVectorCollectionSnapshot(string collection)
    {
        if (!perCollection.TryGetValue(collection, out var counter))
        {
            return new VectorCollectionMetricsSnapshot(collection, 0, 0);
        }
        return SnapshotOf(collection, counter);
    }

    public MetricsSnapshot Snapshot(DateTimeOffset now)
    {
        var perNodeSnapshot = perNode
            .Select(pair => new NodeMetricsSnapshot(
                pair.Key,
                Interlocked.Read(ref pair.Value.Total),
                Math.Max(0, Interlocked.Read(ref pair.Value.InFlight)),
                Interlocked.Read(ref pair.Value.Completed),
                Interlocked.Read(ref pair.Value.Failed)))
            .OrderBy(snapshot => snapshot.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var perCollectionSnapshot = perCollection
            .Select(pair => SnapshotOf(pair.Key, pair.Value))
            .OrderBy(snapshot => snapshot.Collection, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var lastFallbackTicks = Interlocked.Read(ref lastFallbackAtTicks);

        return new MetricsSnapshot(
            (now - StartedAtUtc).TotalSeconds,
            Interlocked.Read(ref requestsTotal),
            Math.Max(0, Interlocked.Read(ref requestsInFlight)),
            Interlocked.Read(ref requestsCompleted),
            Interlocked.Read(ref requestsFailed),
            Interlocked.Read(ref failoversAttempted),
            Interlocked.Read(ref failoversSucceeded),
            Interlocked.Read(ref nodesEvicted),
            Interlocked.Read(ref openAiRequestsTotal),
            Interlocked.Read(ref fallbackDispatched),
            lastFallbackModel,
            lastFallbackTicks == 0 ? null : new DateTimeOffset(lastFallbackTicks, TimeSpan.Zero),
            Interlocked.Read(ref vectorReplicasHealed),
            Interlocked.Read(ref vectorRebuildsFromRaw),
            Interlocked.Read(ref vectorUnderReplicated),
            perNodeSnapshot,
            perCollectionSnapshot);
    }

    private static VectorCollectionMetricsSnapshot SnapshotOf(string collection, VectorCollectionCounter counter)
    {
        var queries = Interlocked.Read(ref counter.Queries);
        var micros = Interlocked.Read(ref counter.QueryLatencyMicrosTotal);
        var avgMs = queries == 0 ? 0.0 : micros / (double)queries / 1000.0;
        var lastIngestTicks = Interlocked.Read(ref counter.LastIngestAtTicks);

        return new VectorCollectionMetricsSnapshot(
            collection,
            queries,
            avgMs,
            Interlocked.Read(ref counter.DocumentsIngested),
            Interlocked.Read(ref counter.ChunksEmbedded),
            Interlocked.Read(ref counter.IngestionFailures),
            lastIngestTicks == 0 ? null : new DateTimeOffset(lastIngestTicks, TimeSpan.Zero),
            counter.LastEmbeddingModel);
    }

    private void DecrementInFlight()
    {
        if (Interlocked.Decrement(ref requestsInFlight) < 0)
        {
            Interlocked.Exchange(ref requestsInFlight, 0);
        }
    }

    private static void DecrementInFlight(NodeCounter counter)
    {
        if (Interlocked.Decrement(ref counter.InFlight) < 0)
        {
            Interlocked.Exchange(ref counter.InFlight, 0);
        }
    }

    private sealed class NodeCounter
    {
        public long Total;
        public long InFlight;
        public long Completed;
        public long Failed;
    }

    private sealed class VectorCollectionCounter
    {
        public long Queries;
        public long QueryLatencyMicrosTotal;
        public long DocumentsIngested;
        public long ChunksEmbedded;
        public long IngestionFailures;
        public long LastIngestAtTicks;
        public volatile string? LastEmbeddingModel;
    }
}

public sealed record MetricsSnapshot(
    double UptimeSeconds,
    long RequestsTotal,
    long RequestsInFlight,
    long RequestsCompleted,
    long RequestsFailed,
    long FailoversAttempted,
    long FailoversSucceeded,
    long NodesEvicted,
    long OpenAiRequestsTotal,
    long FallbackDispatched,
    string? LastFallbackModel,
    DateTimeOffset? LastFallbackAtUtc,
    long VectorReplicasHealed,
    long VectorRebuildsFromRaw,
    long VectorUnderReplicated,
    IReadOnlyList<NodeMetricsSnapshot> PerNode,
    IReadOnlyList<VectorCollectionMetricsSnapshot> PerCollection);

public sealed record NodeMetricsSnapshot(
    string NodeId,
    long RequestsTotal,
    long RequestsInFlight,
    long RequestsCompleted,
    long RequestsFailed);

/// <summary>
/// Per-collection counters. <see cref="DocumentsIngested"/> and <see cref="ChunksEmbedded"/> count
/// what this coordinator has done **since it started** — they are not a census of what is in the
/// store, and a restart resets them to zero exactly like every other counter here. The store's own
/// count is on the documents endpoint, which reads it.
/// </summary>
public sealed record VectorCollectionMetricsSnapshot(
    string Collection,
    long Queries,
    double QueryLatencyAvgMs,
    long DocumentsIngested = 0,
    long ChunksEmbedded = 0,
    long IngestionFailures = 0,
    DateTimeOffset? LastIngestAtUtc = null,
    string? LastEmbeddingModel = null);
