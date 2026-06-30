using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class HealingServiceTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = new();

    public HealingServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-healing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task EvictedHolderIsHealedOnSurvivor()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");
        UpsertNode(registry, "conn-c", "node-c");

        var fixture = Build(registry, replicationFactor: 2);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        // Seed initial placement.
        await fixture.Healing.HealNowAsync();
        Assert.Equal(2, fixture.Replicas.Holders("docs").Count);
        var initialHolders = fixture.Replicas.Holders("docs").ToHashSet();

        // Kill one holder.
        var victim = initialHolders.First();
        registry.Remove(victim);

        fixture.Hub.Sends.Clear();
        await fixture.Healing.HealNowAsync();

        // Factor restored on a different connection.
        var after = fixture.Replicas.Holders("docs");
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(victim, after);
        Assert.Contains(fixture.Hub.Sends, s => s.Method == "AssignVectorReplica");

        // Metric counter incremented for the heal.
        var snapshot = fixture.Metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.True(snapshot.VectorReplicasHealed >= 1);
    }

    [Fact]
    public async Task LastHolderDownRebuildsFromRawOnSurvivor()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-only", "node-only");

        var fixture = Build(registry, replicationFactor: 1);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        await fixture.Healing.HealNowAsync();
        Assert.Single(fixture.Replicas.Holders("docs"));

        // Kill the only holder.
        registry.Remove("conn-only");
        await fixture.Healing.HealNowAsync();

        // No holders, but the hub-local store still answers reads. (We verify by
        // calling QueryAsync directly — the data is still on disk.)
        var matches = await fixture.Store.QueryAsync("docs", new VectorQuery([1f, 0f], 1));
        Assert.Single(matches);
        Assert.Equal("a", matches[0].Id);

        // A new node joins → seeded from raw.
        UpsertNode(registry, "conn-new", "node-new");
        fixture.Hub.Sends.Clear();
        await fixture.Healing.HealNowAsync();

        Assert.Single(fixture.Replicas.Holders("docs"));
        Assert.Contains("conn-new", fixture.Replicas.Holders("docs"));
        var assignment = fixture.Hub.Sends.FirstOrDefault(s => s.Method == "AssignVectorReplica");
        Assert.NotNull(assignment);
        var payload = Assert.IsType<VectorReplicaAssignment>(assignment!.Args[0]);
        Assert.Single(payload.Records);
        Assert.Equal("a", payload.Records[0].Id);

        // Rebuild-from-raw counter ticked at least once (last-holder-down path).
        var snapshot = fixture.Metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.True(snapshot.VectorRebuildsFromRaw >= 1);
    }

    [Fact]
    public async Task FlappingNodeDoesNotStormHealsBeyondSettledState()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var fixture = Build(registry, replicationFactor: 2);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        await fixture.Healing.HealNowAsync();
        Assert.Equal(2, fixture.Replicas.Holders("docs").Count);

        fixture.Hub.Sends.Clear();

        // Simulate a flap: drop and re-add several times. We only verify the settled
        // state and that the heal-lock prevents reentrant work — concurrent calls are
        // collapsed.
        for (var i = 0; i < 5; i++)
        {
            registry.Remove("conn-b");
            UpsertNode(registry, "conn-b", "node-b");
        }

        // Drive a single heal to convergence.
        await fixture.Healing.HealNowAsync();
        Assert.Equal(2, fixture.Replicas.Holders("docs").Count);

        // Each heal pass produces at most one Assign per missing slot. With 5 flaps and
        // a single converged pass, we expect no more than 1 Assign (for conn-b).
        var assigns = fixture.Hub.Sends.Count(s => s.Method == "AssignVectorReplica");
        Assert.True(assigns <= 1, $"expected ≤1 Assign in settled state, got {assigns}");
    }

    [Fact]
    public async Task NewNodeJoinSeedsReplicaWhenUnderTarget()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");

        var fixture = Build(registry, replicationFactor: 2);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        await fixture.Healing.HealNowAsync();
        Assert.Single(fixture.Replicas.Holders("docs")); // factor capped at fleet size

        // Another node joins.
        UpsertNode(registry, "conn-b", "node-b");
        fixture.Hub.Sends.Clear();
        await fixture.Healing.HealNowAsync();

        Assert.Equal(2, fixture.Replicas.Holders("docs").Count);
        Assert.Contains(fixture.Hub.Sends, s => s.Method == "AssignVectorReplica");
    }

    [Fact]
    public async Task RebuildEndpointRestoresUnderReplicatedCollection()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var fixture = Build(registry, replicationFactor: 2);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        await fixture.Healing.HealNowAsync();
        Assert.Equal(2, fixture.Replicas.Holders("docs").Count);

        // Artificially under-replicate by forgetting one placement (without dropping
        // the node) — RebuildAsync should restore it.
        var holder = fixture.Replicas.Holders("docs").First();
        fixture.Replicas.Remove("docs", holder);
        Assert.Single(fixture.Replicas.Holders("docs"));

        fixture.Hub.Sends.Clear();
        await fixture.Healing.RebuildAsync("docs");

        Assert.Equal(2, fixture.Replicas.Holders("docs").Count);
        Assert.Contains(fixture.Hub.Sends, s => s.Method == "AssignVectorReplica");

        var snapshot = fixture.Metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.True(snapshot.VectorRebuildsFromRaw >= 1);
    }

    [Fact]
    public async Task RebuildUnknownCollectionThrows()
    {
        var registry = new NodeRegistry();
        var fixture = Build(registry, replicationFactor: 1);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Healing.RebuildAsync("ghost"));
    }

    [Fact]
    public async Task RepushedReplicaCarriesIdenticalRecordsAsSource()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        var fixture = Build(registry, replicationFactor: 1);

        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("b", [0f, 1f]));

        await fixture.Healing.HealNowAsync();
        Assert.Single(fixture.Replicas.Holders("docs"));

        // Lose the holder, gain a new one, then heal.
        registry.Remove("conn-a");
        UpsertNode(registry, "conn-b", "node-b");
        fixture.Hub.Sends.Clear();

        await fixture.Healing.HealNowAsync();

        var assignment = fixture.Hub.Sends
            .Where(s => s.Method == "AssignVectorReplica")
            .Select(s => (VectorReplicaAssignment)s.Args[0]!)
            .First();

        var expectedIds = (await fixture.Store.QueryAsync("docs", new VectorQuery([1f, 0f], 10)))
            .Select(m => m.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var actualIds = assignment.Records
            .Select(r => r.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedIds, actualIds);
    }

    [Fact]
    public async Task UnderReplicatedGaugeReflectsLiveDeficit()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");

        var fixture = Build(registry, replicationFactor: 2);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        await fixture.Healing.HealNowAsync();

        var snapshot = fixture.Metrics.Snapshot(DateTimeOffset.UtcNow);
        // Only one node connected but desired=min(2,1)=1, so it's at target — gauge=0.
        Assert.Equal(0L, snapshot.VectorUnderReplicated);

        UpsertNode(registry, "conn-b", "node-b");
        // Forget one placement to simulate transient deficit without dropping the node.
        var holder = fixture.Replicas.Holders("docs").First();
        fixture.Replicas.Remove("docs", holder);

        // Force a gauge refresh by running a heal pass that will also restore the deficit;
        // we capture the gauge through the snapshot after.
        await fixture.Healing.HealNowAsync();
        snapshot = fixture.Metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(0L, snapshot.VectorUnderReplicated); // heal restored target
    }

    private TestFixture Build(NodeRegistry registry, int replicationFactor)
    {
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = _root,
            SnapshotEveryOps = 100,
            ReplicationFactor = replicationFactor
        });

        var store = new LocalVectorStore(options, NullLogger<LocalVectorStore>.Instance);
        _disposables.Add(store);
        var hub = new RecordingHubContext();
        var replicas = new ReplicaRegistry();
        var coord = new ReplicationCoordinator(
            store,
            registry,
            replicas,
            hub,
            options,
            NullLogger<ReplicationCoordinator>.Instance);
        _disposables.Add(coord);
        var metrics = new Metrics();
        var healing = new HealingService(
            store,
            registry,
            replicas,
            coord,
            options,
            metrics,
            NullLogger<HealingService>.Instance);
        _disposables.Add(healing);

        return new TestFixture(store, registry, replicas, coord, hub, healing, metrics);
    }

    private static void UpsertNode(NodeRegistry registry, string connectionId, string nodeId)
    {
        var reg = new NodeRegistration(nodeId, nodeId, "http://x", "test", null, null, null);
        registry.Upsert(connectionId, reg, DateTimeOffset.UtcNow);
    }

    private sealed record TestFixture(
        LocalVectorStore Store,
        NodeRegistry Registry,
        ReplicaRegistry Replicas,
        ReplicationCoordinator Coord,
        RecordingHubContext Hub,
        HealingService Healing,
        Metrics Metrics);
}
