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

    // ---- phase 69 -------------------------------------------------------------------------------

    /// <summary>
    /// 69 D1/D2. The node that is up and cannot answer: it keeps its registration, keeps its models
    /// and stops being somewhere the router may send work.
    /// </summary>
    [Theory]
    [InlineData(BackendHealth.Unreachable)]
    [InlineData(BackendHealth.Wedged)]
    public void AnUnhealthyBackendTakesTheNodeOutOfTheCandidateSetWithoutTakingItOutOfTheFleet(BackendHealth health)
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: health), now);

        Assert.Empty(registry.FindNodesWithModel("llama3"));

        // Still connected, still holding the model, and the console can still see why.
        var node = Assert.Single(registry.Snapshot(now));
        Assert.Equal(health, node.BackendHealth);
        Assert.Equal(1, node.ModelCount);
        Assert.Single(registry.DistinctModels());
    }

    /// <summary>
    /// 69 D2. Possession is a different question from serviceability, and placement, discovery and
    /// the refusal that has to tell a dead server from a missing model all ask the first one.
    /// </summary>
    [Fact]
    public void ASickNodeStillHoldsItsModelsForAnybodyAskingAboutPossession()
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Wedged), now);

        Assert.Empty(registry.FindNodesWithModel("llama3"));
        Assert.Single(registry.FindNodesWithModel("llama3", includeUnserviceable: true));
    }

    /// <summary>
    /// 69 D5, and the one that would make the release notorious if it broke: an upgrade must not
    /// empty a fleet of nodes too old to have the field.
    /// </summary>
    [Fact]
    public void ANodeThatDeclaresNoHealthIsRoutableExactlyAsItWasBefore()
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0), now);

        Assert.Single(registry.FindNodesWithModel("llama3"));
        Assert.Null(Assert.Single(registry.Snapshot(now)).BackendHealth);
    }

    /// <summary>69 D1. Recovery needs nothing but the next heartbeat.</summary>
    [Fact]
    public void RecoveryPutsTheNodeBackWithNoRestartAndNoReRegistration()
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Unreachable), now);
        Assert.Empty(registry.FindNodesWithModel("llama3"));

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Healthy), now.AddSeconds(15));

        Assert.Single(registry.FindNodesWithModel("llama3"));
    }

    /// <summary>
    /// 69 D6. A heartbeat every few seconds must not re-render the console; the transition is the
    /// only interesting moment, and it is the one that fires.
    /// </summary>
    [Fact]
    public void OnlyAHealthTransitionRaisesChangedAndARepeatedVerdictDoesNot()
    {
        var registry = WithModel(out var now);

        var count = 0;
        registry.Changed += () => count++;

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Healthy), now);
        Assert.Equal(1, count);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 1, Backend: BackendHealth.Healthy), now.AddSeconds(15));
        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 2, Backend: BackendHealth.Healthy), now.AddSeconds(30));
        Assert.Equal(1, count);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Wedged), now.AddSeconds(45));
        Assert.Equal(2, count);
    }

    /// <summary>
    /// 69, and the reason v3.36.1 exists. The node reports zero models when its backend is down
    /// (36 D7) and that report still arrives — so without this, the model left the registry one
    /// refresh interval after the backend died and the refusal reverted to the `404 model not
    /// found` the whole phase exists to remove. Measured on the published 3.36.0 image: the 503
    /// held for six seconds.
    /// </summary>
    [Fact]
    public void AnEmptyReportFromASickNodeDoesNotEraseTheModelsTheRefusalNeedsToNameThem()
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Unreachable), now);
        registry.ReportModels("connection-1", new NodeModels("node-1", [], now.AddSeconds(20)), now.AddSeconds(20));

        // Held, so the hub can still say *which* model it is refusing and why...
        Assert.Single(registry.FindNodesWithModel("llama3", includeUnserviceable: true));
        Assert.Single(registry.DistinctModels());

        // ...and still refuses to route there, which is what the health field is for.
        Assert.Empty(registry.FindNodesWithModel("llama3"));
    }

    [Fact]
    public void AHealthyNodeThatReportsNoModelsIsStillEmptiedExactlyAsBefore()
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Healthy), now);
        registry.ReportModels("connection-1", new NodeModels("node-1", [], now.AddSeconds(20)), now.AddSeconds(20));

        // A box whose models were genuinely deleted says so, and a node that never declares health
        // — anything older than v3.36 — takes this same path.
        Assert.Empty(registry.DistinctModels());
    }

    [Fact]
    public void RecoveryReplacesTheHeldInventoryWithWhatTheNodeActuallyHasNow()
    {
        var registry = WithModel(out var now);

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Wedged), now);
        registry.ReportModels("connection-1", new NodeModels("node-1", [], now.AddSeconds(20)), now.AddSeconds(20));

        registry.Touch("connection-1", new Heartbeat("node-1", now, InFlight: 0, Backend: BackendHealth.Healthy), now.AddSeconds(30));
        registry.ReportModels(
            "connection-1",
            new NodeModels("node-1", [new ModelInfo("mistral", "digest-2", 456)], now.AddSeconds(40)),
            now.AddSeconds(40));

        // The stale list is not merged with the live one — it is replaced, which is what makes
        // holding it safe in the first place.
        Assert.Equal("mistral", Assert.Single(registry.DistinctModels()).Name);
    }

    private static NodeRegistry WithModel(out DateTimeOffset now)
    {
        var registry = new NodeRegistry();
        now = DateTimeOffset.UtcNow;

        registry.Upsert("connection-1", Registration("node-1"), now);
        registry.ReportModels(
            "connection-1",
            new NodeModels("node-1", [new ModelInfo("llama3", "digest-1", 123)], now),
            now);

        return registry;
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
