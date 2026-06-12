using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

public class NodeRegistryTests
{
    [Fact]
    public void UpsertCreatesSnapshotEntry()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        registry.Upsert("connection-1", Registration("node-1"), now);

        var node = Assert.Single(registry.Snapshot(now));
        Assert.Equal("connection-1", node.ConnectionId);
        Assert.Equal("node-1", node.NodeId);
        Assert.Equal("local-node", node.Name);
        Assert.Equal("http://localhost:11434/", node.OllamaEndpoint);
        Assert.Equal("0.2.0", node.Version);
        Assert.Equal(now, node.LastSeenUtc);
        Assert.Equal(0, node.InFlight);
    }

    [Fact]
    public void TouchUpdatesLastSeenAndInFlight()
    {
        var registry = new NodeRegistry();
        var initial = DateTimeOffset.UtcNow;
        var touched = initial.AddSeconds(5);

        registry.Upsert("connection-1", Registration("node-1"), initial);

        var updated = registry.Touch(
            "connection-1",
            new Heartbeat("node-1", touched.AddSeconds(-1), InFlight: 3),
            touched);

        Assert.True(updated);

        var node = Assert.Single(registry.Snapshot(touched));
        Assert.Equal(touched, node.LastSeenUtc);
        Assert.Equal(3, node.InFlight);
    }

    [Fact]
    public void RemoveDeletesByConnectionId()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var removed = registry.Remove("connection-1");

        Assert.True(removed);
        Assert.Empty(registry.Snapshot(now));
    }

    [Fact]
    public void EvictStaleRemovesNodesOlderThanCutoff()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("stale-connection", Registration("stale-node"), now.AddSeconds(-31));
        registry.Upsert("fresh-connection", Registration("fresh-node"), now.AddSeconds(-10));

        var evicted = registry.EvictStale(now.AddSeconds(-30), now);

        var node = Assert.Single(evicted);
        Assert.Equal("stale-node", node.NodeId);

        var remaining = Assert.Single(registry.Snapshot(now));
        Assert.Equal("fresh-node", remaining.NodeId);
    }

    private static NodeRegistration Registration(string nodeId)
    {
        return new NodeRegistration(
            nodeId,
            "local-node",
            "http://localhost:11434/",
            "0.2.0");
    }
}
