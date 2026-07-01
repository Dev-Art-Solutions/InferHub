using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class VectorEventEmissionTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = new();

    public VectorEventEmissionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-vevents-" + Guid.NewGuid().ToString("N"));
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
    public async Task ReplicaAssignedAndHealEventsFireOnHealPass()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");

        var fixture = Build(registry, replicationFactor: 1);
        var events = new List<VectorEvent>();
        using var sub = fixture.Events.Subscribe(ev => { lock (events) events.Add(ev); });

        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        await fixture.Healing.HealNowAsync();

        Assert.Contains(events, e => e.Kind == "vector.replica.assigned" && e.Collection == "docs");
        Assert.Contains(events, e => e.Kind == "vector.heal.started" && e.Collection == "docs");
        Assert.Contains(events, e => e.Kind == "vector.heal.completed" && e.Collection == "docs");
    }

    [Fact]
    public async Task CollectionCreatedEventFiresWhenCoordinatorIsStarted()
    {
        var registry = new NodeRegistry();
        var fixture = Build(registry, replicationFactor: 1);

        // In production wiring the ReplicationCoordinator hosts subscribe to
        // LocalVectorStore.CollectionCreated in StartAsync. Simulate that here.
        await fixture.Coord.StartAsync(CancellationToken.None);

        var events = new List<VectorEvent>();
        using var sub = fixture.Events.Subscribe(ev => { lock (events) events.Add(ev); });

        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        Assert.Contains(events, e => e.Kind == "vector.collection.created" && e.Collection == "docs");
    }

    [Fact]
    public async Task ReplicaLostEventFiresWhenStaleHolderIsReclaimedByRecompute()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var fixture = Build(registry, replicationFactor: 1);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Healing.HealNowAsync();
        Assert.Single(fixture.Replicas.Holders("docs"));
        var initialHolder = fixture.Replicas.Holders("docs").First();

        // Drop the sole holder — the surviving node lets recompute reach
        // PlaceForCollectionAsync which detects the stale holder and publishes
        // vector.replica.lost while re-seeding onto the survivor.
        var events = new List<VectorEvent>();
        using var sub = fixture.Events.Subscribe(ev => { lock (events) events.Add(ev); });
        registry.Remove(initialHolder);

        await fixture.Healing.HealNowAsync();

        Assert.Contains(events, e => e.Kind == "vector.replica.lost" && e.Collection == "docs");
        Assert.Contains(events, e => e.Kind == "vector.replica.assigned" && e.Collection == "docs");
    }

    [Fact]
    public async Task RebuildEndpointEmitsHealStartedAndCompleted()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var fixture = Build(registry, replicationFactor: 2);
        await fixture.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await fixture.Healing.HealNowAsync();

        var events = new List<VectorEvent>();
        using var sub = fixture.Events.Subscribe(ev => { lock (events) events.Add(ev); });

        await fixture.Healing.RebuildAsync("docs");

        Assert.Contains(events, e => e.Kind == "vector.heal.started" && e.Data.ContainsKey("reason") && (string)e.Data["reason"]! == "rebuild");
        Assert.Contains(events, e => e.Kind == "vector.heal.completed" && e.Collection == "docs");
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

        var events = new VectorEvents();
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
            NullLogger<ReplicationCoordinator>.Instance,
            events);
        _disposables.Add(coord);
        var metrics = new Metrics();
        var healing = new HealingService(
            store,
            registry,
            replicas,
            coord,
            options,
            metrics,
            NullLogger<HealingService>.Instance,
            events);
        _disposables.Add(healing);

        return new TestFixture(store, registry, replicas, coord, healing, metrics, events);
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
        HealingService Healing,
        Metrics Metrics,
        VectorEvents Events);
}
