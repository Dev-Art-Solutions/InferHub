using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class ReplicationCoordinatorTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _disposables = new();

    public ReplicationCoordinatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-replication-" + Guid.NewGuid().ToString("N"));
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
    public async Task RecomputeAssignsReplicasUpToFactor()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var subject = NewSubject(registry, replicationFactor: 2, out var store, out var hub);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

        await subject.RecomputeAsync("docs");

        var holders = subject.Holders("docs");
        Assert.Equal(2, holders.Count);
        Assert.Contains("conn-a", holders);
        Assert.Contains("conn-b", holders);

        // Two assignments were sent, one to each holder, each carrying the snapshot.
        var assignments = hub.Sends.Where(s => s.Method == "AssignVectorReplica").ToArray();
        Assert.Equal(2, assignments.Length);
        foreach (var send in assignments)
        {
            var assignment = Assert.IsType<VectorReplicaAssignment>(send.Args[0]);
            Assert.Equal("docs", assignment.Collection);
            Assert.Single(assignment.Records);
            Assert.Equal("a", assignment.Records[0].Id);
        }
    }

    [Fact]
    public async Task UpsertAfterPlacementFansOutToHolders()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var subject = NewSubject(registry, replicationFactor: 2, out var store, out var hub);

        // Start the subject so it subscribes to store events.
        await subject.StartAsync(CancellationToken.None);
        try
        {
            await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
            await subject.RecomputeAsync("docs");

            hub.Sends.Clear();
            await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));

            // The CollectionCreated/RecordUpserted handlers are async void; spin briefly.
            await WaitFor(() => hub.Sends.Count(s => s.Method == "ApplyVectorOp") >= 2);

            var ops = hub.Sends.Where(s => s.Method == "ApplyVectorOp").ToArray();
            Assert.Equal(2, ops.Length);
            foreach (var send in ops)
            {
                var op = Assert.IsType<VectorReplicaOp>(send.Args[0]);
                Assert.Equal("upsert", op.Op);
                Assert.Equal("a", op.Id);
            }
        }
        finally
        {
            await subject.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReleasingNodeFromFleetDropsItsReplica()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");

        var subject = NewSubject(registry, replicationFactor: 2, out var store, out var hub);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await subject.RecomputeAsync("docs");
        Assert.Equal(2, subject.Holders("docs").Count);

        registry.Remove("conn-b");
        await subject.RecomputeAsync("docs");

        Assert.Single(subject.Holders("docs"));
        Assert.Contains("conn-a", subject.Holders("docs"));
    }

    [Fact]
    public async Task ReplicationFactorBeyondFleetSizeIsCapped()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-only", "node-only");

        var subject = NewSubject(registry, replicationFactor: 3, out var store, out _);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await subject.RecomputeAsync("docs");

        Assert.Single(subject.Holders("docs"));
    }

    [Fact]
    public async Task ApplyInventoryRecognisesUpToDateReplicaWithoutRepush()
    {
        var registry = new NodeRegistry();
        var subject = NewSubject(registry, replicationFactor: 1, out var store, out var hub);

        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));
        var current = (await store.GetCollectionAsync("docs"))!;

        // Simulate a node reconnect: register with inventory matching the hub's current seqNo.
        UpsertNode(registry, "conn-restart", "node-r");
        subject.ApplyInventory("conn-restart", new[]
        {
            new NodeReplicaInventoryItem("docs", current.Operations)
        });

        hub.Sends.Clear();
        await subject.RecomputeAsync("docs");

        // No AssignVectorReplica should be sent — the inventory was recognised as up-to-date.
        Assert.DoesNotContain(hub.Sends, s => s.Method == "AssignVectorReplica");
        Assert.Contains("conn-restart", subject.Holders("docs"));
    }

    [Fact]
    public async Task ApplyInventoryWithStaleSeqTriggersRepush()
    {
        var registry = new NodeRegistry();
        var subject = NewSubject(registry, replicationFactor: 1, out var store, out var hub);

        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));
        await store.UpsertAsync("docs", new VectorUpsert("b", [0f, 1f]));

        UpsertNode(registry, "conn-stale", "node-s");
        // Inventory reports an older lastSeq — must NOT register as a holder.
        subject.ApplyInventory("conn-stale", new[]
        {
            new NodeReplicaInventoryItem("docs", 1)
        });

        hub.Sends.Clear();
        await subject.RecomputeAsync("docs");

        Assert.Contains(hub.Sends, s => s.Method == "AssignVectorReplica");
    }

    [Fact]
    public async Task CordonedNodesAreNotPickedAsHolders()
    {
        var registry = new NodeRegistry();
        UpsertNode(registry, "conn-a", "node-a");
        UpsertNode(registry, "conn-b", "node-b");
        registry.Cordon("node-b");

        var subject = NewSubject(registry, replicationFactor: 2, out var store, out _);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await subject.RecomputeAsync("docs");

        Assert.Single(subject.Holders("docs"));
        Assert.Contains("conn-a", subject.Holders("docs"));
    }

    private ReplicationCoordinator NewSubject(
        NodeRegistry registry,
        int replicationFactor,
        out LocalVectorStore store,
        out RecordingHubContext hub)
    {
        store = new LocalVectorStore(
            Options.Create(new VectorStoreOptions { Enabled = true, DataDirectory = _root, SnapshotEveryOps = 100, ReplicationFactor = replicationFactor }),
            NullLogger<LocalVectorStore>.Instance);
        _disposables.Add(store);
        hub = new RecordingHubContext();
        var replicas = new ReplicaRegistry();
        var coord = new ReplicationCoordinator(
            store,
            registry,
            replicas,
            hub,
            Options.Create(new VectorStoreOptions { ReplicationFactor = replicationFactor }),
            NullLogger<ReplicationCoordinator>.Instance);
        _disposables.Add(coord);
        return coord;
    }

    private static void UpsertNode(NodeRegistry registry, string connectionId, string nodeId)
    {
        var reg = new NodeRegistration(nodeId, nodeId, "http://x", "test", null, null, null);
        registry.Upsert(connectionId, reg, DateTimeOffset.UtcNow);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not satisfied within timeout");
    }
}

internal sealed record SentMessage(string ConnectionId, string Method, object?[] Args);

internal sealed class RecordingHubContext : IHubContext<NodeHub>
{
    public List<SentMessage> Sends { get; } = new();

    public IHubClients Clients { get; }

    public IGroupManager Groups => throw new NotImplementedException();

    public RecordingHubContext()
    {
        Clients = new RecordingHubClients(this);
    }
}

internal sealed class RecordingHubClients(RecordingHubContext parent) : IHubClients
{
    public IClientProxy All => throw new NotImplementedException();
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Client(string connectionId) => new RecordingClientProxy(parent, connectionId);
    public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
    public IClientProxy Group(string groupName) => throw new NotImplementedException();
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
    public IClientProxy User(string userId) => throw new NotImplementedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
}

internal sealed class RecordingClientProxy(RecordingHubContext parent, string connectionId) : IClientProxy
{
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        lock (parent.Sends)
        {
            parent.Sends.Add(new SentMessage(connectionId, method, args));
        }
        return Task.CompletedTask;
    }
}
