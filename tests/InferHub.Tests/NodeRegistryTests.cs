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
        Assert.Equal(0, node.ModelCount);
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
    public void ReportModelsUpdatesSnapshotModelCount()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var reported = registry.ReportModels(
            "connection-1",
            new NodeModels(
                "node-1",
                [new ModelInfo("llama3", "digest-1", 123)],
                now.AddSeconds(1)),
            now.AddSeconds(1));

        Assert.True(reported);

        var node = Assert.Single(registry.Snapshot(now.AddSeconds(1)));
        Assert.Equal(1, node.ModelCount);
    }

    [Fact]
    public void ReportModelsReturnsFalseForUnknownConnection()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;

        var reported = registry.ReportModels(
            "missing",
            new NodeModels("node-1", [new ModelInfo("llama3", null, null)], now),
            now);

        Assert.False(reported);
        Assert.Empty(registry.DistinctModels());
    }

    [Fact]
    public void DistinctModelsDeDuplicatesByName()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-b", Registration("node-b", "beta-node"), now);
        registry.Upsert("connection-a", Registration("node-a", "alpha-node"), now);

        registry.ReportModels(
            "connection-b",
            new NodeModels(
                "node-b",
                [
                    new ModelInfo("qwen2", "digest-qwen", 200),
                    new ModelInfo("llama3", "digest-beta", 300)
                ],
                now),
            now);

        registry.ReportModels(
            "connection-a",
            new NodeModels(
                "node-a",
                [new ModelInfo("LLAMA3", "digest-alpha", 100)],
                now),
            now);

        var models = registry.DistinctModels().ToArray();

        Assert.Collection(
            models,
            model =>
            {
                Assert.Equal("LLAMA3", model.Name);
                Assert.Equal("digest-alpha", model.Digest);
            },
            model => Assert.Equal("qwen2", model.Name));
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
        Assert.Empty(registry.DistinctModels());
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

    [Fact]
    public void EvictStaleRemovesReportedModels()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("stale-connection", Registration("stale-node"), now.AddSeconds(-31));
        registry.ReportModels(
            "stale-connection",
            new NodeModels("stale-node", [new ModelInfo("llama3", "digest-1", 123)], now.AddSeconds(-31)),
            now.AddSeconds(-31));

        registry.EvictStale(now.AddSeconds(-30), now);

        Assert.Empty(registry.DistinctModels());
    }

    [Fact]
    public void IncrementAndDecrementInFlightTracksCoordinatorView()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        Assert.Equal(0, registry.GetLocalInFlight("connection-1"));

        Assert.Equal(1, registry.IncrementInFlight("connection-1"));
        Assert.Equal(2, registry.IncrementInFlight("connection-1"));
        Assert.Equal(2, registry.GetLocalInFlight("connection-1"));

        Assert.Equal(1, registry.DecrementInFlight("connection-1"));
        Assert.Equal(0, registry.DecrementInFlight("connection-1"));
    }

    [Fact]
    public void DecrementInFlightClampsAtZero()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        Assert.Equal(0, registry.DecrementInFlight("connection-1"));
        Assert.Equal(0, registry.GetLocalInFlight("connection-1"));
    }

    [Fact]
    public void RemoveClearsInFlightCounter()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);
        registry.IncrementInFlight("connection-1");

        registry.Remove("connection-1");

        Assert.Equal(0, registry.GetLocalInFlight("connection-1"));
    }

    [Fact]
    public void SnapshotIncludesBothReportedAndLocalInFlight()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 7), now);
        registry.IncrementInFlight("connection-1");
        registry.IncrementInFlight("connection-1");

        var snapshot = Assert.Single(registry.Snapshot(now));
        Assert.Equal(7, snapshot.InFlight);
        Assert.Equal(2, snapshot.LocalInFlight);
    }

    [Fact]
    public void CordonMarksNodeAsCordonedInSnapshot()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var cordoned = registry.Cordon("node-1");

        Assert.True(cordoned);
        var snapshot = Assert.Single(registry.Snapshot(now));
        Assert.True(snapshot.Cordoned);
    }

    [Fact]
    public void UncordonClearsCordonState()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);
        registry.Cordon("node-1");

        var uncordoned = registry.Uncordon("node-1");

        Assert.True(uncordoned);
        var snapshot = Assert.Single(registry.Snapshot(now));
        Assert.False(snapshot.Cordoned);
    }

    [Fact]
    public void CordonReturnsFalseForUnknownNode()
    {
        var registry = new NodeRegistry();

        Assert.False(registry.Cordon("missing"));
        Assert.False(registry.Uncordon("missing"));
    }

    [Fact]
    public void CordonIsCaseInsensitive()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("Node-1"), now);

        Assert.True(registry.Cordon("node-1"));
        Assert.True(Assert.Single(registry.Snapshot(now)).Cordoned);
    }

    [Fact]
    public void CordonedNodeIsExcludedFromFindNodesWithModel()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);
        registry.ReportModels(
            "connection-1",
            new NodeModels("node-1", [new ModelInfo("llama3", "digest", 1)], now),
            now);

        Assert.Single(registry.FindNodesWithModel("llama3"));

        registry.Cordon("node-1");
        Assert.Empty(registry.FindNodesWithModel("llama3"));

        registry.Uncordon("node-1");
        Assert.Single(registry.FindNodesWithModel("llama3"));
    }

    [Fact]
    public void UpsertPreservesExistingCordonState()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);
        registry.Cordon("node-1");

        // A reconnect or re-registration shouldn't silently uncordon the node.
        registry.Upsert("connection-1", Registration("node-1", "renamed"), now.AddSeconds(1));

        var snapshot = Assert.Single(registry.Snapshot(now.AddSeconds(1)));
        Assert.True(snapshot.Cordoned);
    }

    [Fact]
    public void FindConnectionIdByNodeIdReturnsConnectionId()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        Assert.Equal("connection-1", registry.FindConnectionIdByNodeId("node-1"));
        Assert.Equal("connection-1", registry.FindConnectionIdByNodeId("NODE-1"));
        Assert.Null(registry.FindConnectionIdByNodeId("missing"));
    }

    [Fact]
    public void RemoveActsAsDeregister()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var connectionId = registry.FindConnectionIdByNodeId("node-1");
        Assert.NotNull(connectionId);

        Assert.True(registry.Remove(connectionId));
        Assert.Empty(registry.Snapshot(now));
        Assert.Null(registry.FindConnectionIdByNodeId("node-1"));
    }

    [Fact]
    public void ChangedFiresOnUpsert()
    {
        var registry = new NodeRegistry();
        var count = 0;
        registry.Changed += () => count++;

        registry.Upsert("connection-1", Registration("node-1"), DateTimeOffset.UtcNow);

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChangedFiresOnRemove()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var count = 0;
        registry.Changed += () => count++;

        var removed = registry.Remove("connection-1");

        Assert.True(removed);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ChangedDoesNotFireWhenRemoveTargetsUnknownConnection()
    {
        var registry = new NodeRegistry();
        var count = 0;
        registry.Changed += () => count++;

        var removed = registry.Remove("missing");

        Assert.False(removed);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ChangedFiresOnCordonTransitionOnly()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var count = 0;
        registry.Changed += () => count++;

        Assert.True(registry.Cordon("node-1"));
        Assert.Equal(1, count);

        // Re-cordoning an already-cordoned node still returns true but should not re-fire.
        Assert.True(registry.Cordon("node-1"));
        Assert.Equal(1, count);

        Assert.True(registry.Uncordon("node-1"));
        Assert.Equal(2, count);
    }

    [Fact]
    public void ChangedDoesNotFireOnHeartbeatTouch()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var count = 0;
        registry.Changed += () => count++;

        Assert.True(registry.Touch(
            "connection-1",
            new Heartbeat("node-1", now, InFlight: 3),
            now.AddSeconds(1)));

        Assert.Equal(0, count);
    }

    [Fact]
    public void ChangedFiresOnReportModels()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("connection-1", Registration("node-1"), now);

        var count = 0;
        registry.Changed += () => count++;

        Assert.True(registry.ReportModels(
            "connection-1",
            new NodeModels("node-1", [new ModelInfo("llama3", "digest", 1)], now),
            now));

        Assert.Equal(1, count);
    }

    [Fact]
    public void ChangedFiresOnEvictStaleWhenAnyEvicted()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("stale-connection", Registration("stale-node"), now.AddSeconds(-31));
        registry.Upsert("fresh-connection", Registration("fresh-node"), now.AddSeconds(-10));

        var count = 0;
        registry.Changed += () => count++;

        var evicted = registry.EvictStale(now.AddSeconds(-30), now);

        Assert.Single(evicted);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ChangedDoesNotFireOnEvictStaleWhenNothingEvicted()
    {
        var registry = new NodeRegistry();
        var now = DateTimeOffset.UtcNow;
        registry.Upsert("fresh-connection", Registration("fresh-node"), now);

        var count = 0;
        registry.Changed += () => count++;

        var evicted = registry.EvictStale(now.AddSeconds(-30), now);

        Assert.Empty(evicted);
        Assert.Equal(0, count);
    }

    private static NodeRegistration Registration(string nodeId, string name = "local-node")
    {
        return new NodeRegistration(
            nodeId,
            name,
            "http://localhost:11434/",
            "0.2.0");
    }
}
