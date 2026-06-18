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
}
