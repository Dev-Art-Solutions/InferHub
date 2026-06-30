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
    private long vectorReplicasHealed;
    private long vectorRebuildsFromRaw;
    private long vectorUnderReplicated;

    private readonly ConcurrentDictionary<string, NodeCounter> perNode = new(StringComparer.OrdinalIgnoreCase);

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

    public void RecordVectorReplicaHealed() => Interlocked.Increment(ref vectorReplicasHealed);

    public void RecordVectorRebuildFromRaw() => Interlocked.Increment(ref vectorRebuildsFromRaw);

    public void SetVectorUnderReplicated(long count) => Interlocked.Exchange(ref vectorUnderReplicated, Math.Max(0, count));

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

        return new MetricsSnapshot(
            (now - StartedAtUtc).TotalSeconds,
            Interlocked.Read(ref requestsTotal),
            Math.Max(0, Interlocked.Read(ref requestsInFlight)),
            Interlocked.Read(ref requestsCompleted),
            Interlocked.Read(ref requestsFailed),
            Interlocked.Read(ref failoversAttempted),
            Interlocked.Read(ref failoversSucceeded),
            Interlocked.Read(ref nodesEvicted),
            Interlocked.Read(ref vectorReplicasHealed),
            Interlocked.Read(ref vectorRebuildsFromRaw),
            Interlocked.Read(ref vectorUnderReplicated),
            perNodeSnapshot);
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
    long VectorReplicasHealed,
    long VectorRebuildsFromRaw,
    long VectorUnderReplicated,
    IReadOnlyList<NodeMetricsSnapshot> PerNode);

public sealed record NodeMetricsSnapshot(
    string NodeId,
    long RequestsTotal,
    long RequestsInFlight,
    long RequestsCompleted,
    long RequestsFailed);
