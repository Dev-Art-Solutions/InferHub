using InferHub.Coordinator.Observability;

namespace InferHub.Tests;

public class MetricsTests
{
    [Fact]
    public void RecordRequestStartTracksTotalAndInFlight()
    {
        var metrics = new Metrics();

        metrics.RecordRequestStart("node-a");
        metrics.RecordRequestStart("node-a");
        metrics.RecordRequestStart("node-b");

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(3, snapshot.RequestsTotal);
        Assert.Equal(3, snapshot.RequestsInFlight);

        var nodeA = snapshot.PerNode.Single(node => node.NodeId == "node-a");
        Assert.Equal(2, nodeA.RequestsTotal);
        Assert.Equal(2, nodeA.RequestsInFlight);
    }

    [Fact]
    public void RecordRequestCompleteDecrementsInFlightAndIncrementsCompleted()
    {
        var metrics = new Metrics();

        metrics.RecordRequestStart("node-a");
        metrics.RecordRequestComplete("node-a");

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.RequestsTotal);
        Assert.Equal(0, snapshot.RequestsInFlight);
        Assert.Equal(1, snapshot.RequestsCompleted);

        var nodeA = snapshot.PerNode.Single(n => n.NodeId == "node-a");
        Assert.Equal(0, nodeA.RequestsInFlight);
        Assert.Equal(1, nodeA.RequestsCompleted);
    }

    [Fact]
    public void RecordRequestFailDecrementsInFlightAndIncrementsFailed()
    {
        var metrics = new Metrics();

        metrics.RecordRequestStart("node-a");
        metrics.RecordRequestFail("node-a");

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(0, snapshot.RequestsInFlight);
        Assert.Equal(1, snapshot.RequestsFailed);
    }

    [Fact]
    public void FailoverAndEvictionCountersAggregate()
    {
        var metrics = new Metrics();

        metrics.RecordFailoverAttempted();
        metrics.RecordFailoverAttempted();
        metrics.RecordFailoverSucceeded();
        metrics.RecordNodeEvicted();

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(2, snapshot.FailoversAttempted);
        Assert.Equal(1, snapshot.FailoversSucceeded);
        Assert.Equal(1, snapshot.NodesEvicted);
    }

    [Fact]
    public void SnapshotIncludesUptime()
    {
        var metrics = new Metrics();
        var later = metrics.StartedAtUtc.AddSeconds(7);

        var snapshot = metrics.Snapshot(later);

        Assert.InRange(snapshot.UptimeSeconds, 6.99, 7.01);
    }

    [Fact]
    public void RecordVectorQueryAggregatesCountAndAverageLatency()
    {
        var metrics = new Metrics();

        metrics.RecordVectorQuery("docs", TimeSpan.FromMilliseconds(10));
        metrics.RecordVectorQuery("docs", TimeSpan.FromMilliseconds(30));
        metrics.RecordVectorQuery("other", TimeSpan.FromMilliseconds(5));

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        var docs = snapshot.PerCollection.Single(c => c.Collection == "docs");
        Assert.Equal(2, docs.Queries);
        Assert.InRange(docs.QueryLatencyAvgMs, 19.9, 20.1);

        var other = snapshot.PerCollection.Single(c => c.Collection == "other");
        Assert.Equal(1, other.Queries);
        Assert.InRange(other.QueryLatencyAvgMs, 4.9, 5.1);

        var single = metrics.GetVectorCollectionSnapshot("docs");
        Assert.Equal("docs", single.Collection);
        Assert.Equal(2, single.Queries);
        Assert.InRange(single.QueryLatencyAvgMs, 19.9, 20.1);
    }

    [Fact]
    public void GetVectorCollectionSnapshotReturnsZeroForUnknownCollection()
    {
        var metrics = new Metrics();
        var snapshot = metrics.GetVectorCollectionSnapshot("ghost");
        Assert.Equal("ghost", snapshot.Collection);
        Assert.Equal(0, snapshot.Queries);
        Assert.Equal(0.0, snapshot.QueryLatencyAvgMs);
    }
}
