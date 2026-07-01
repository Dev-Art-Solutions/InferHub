using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class StatusVectorBlockTests : IDisposable
{
    private readonly string _root;
    private readonly LocalVectorStore _store;

    public StatusVectorBlockTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = _root,
            SnapshotEveryOps = 100
        });
        _store = new LocalVectorStore(options, NullLogger<LocalVectorStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task VectorBlockReflectsCollectionRecordCountAndDistance()
    {
        await _store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await _store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f]));
        await _store.UpsertAsync("docs", new VectorUpsert("b", [0f, 1f]));

        var collections = await _store.ListCollectionsAsync();
        var replicas = new ReplicaRegistry();
        var nodes = Array.Empty<NodeSnapshot>();

        var block = StatusEndpoint.BuildVectorBlock(collections, replicas, nodes, replicationFactor: 2);

        var docs = Assert.Single(block.Collections);
        Assert.Equal("docs", docs.Name);
        Assert.Equal(2, docs.Dimension);
        Assert.Equal("cosine", docs.Distance);
        Assert.Equal(2, docs.RecordCount);
        Assert.Equal(2, docs.TargetReplicas);
        Assert.Equal(0, docs.LiveReplicas);
        Assert.Empty(docs.ReplicaNodes);
        // Zero nodes online → desired = min(2, 0) = 0, so LiveReplicas (0) < desired (0) is false.
        Assert.False(docs.UnderReplicated);
    }

    [Fact]
    public async Task VectorBlockFlagsUnderReplicated()
    {
        await _store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        var replicas = new ReplicaRegistry();
        // Two nodes connected but only one holds a replica → under target.
        var nodes = new[]
        {
            NewNodeSnapshot("conn-a", "node-a"),
            NewNodeSnapshot("conn-b", "node-b")
        };
        replicas.Add("docs", "conn-a");

        var collections = await _store.ListCollectionsAsync();
        var block = StatusEndpoint.BuildVectorBlock(collections, replicas, nodes, replicationFactor: 2);

        var docs = Assert.Single(block.Collections);
        Assert.Equal(2, docs.TargetReplicas);
        Assert.Equal(1, docs.LiveReplicas);
        Assert.Equal(new[] { "node-a" }, docs.ReplicaNodes);
        Assert.True(docs.UnderReplicated);
    }

    [Fact]
    public async Task VectorBlockAtTargetWhenReplicasMatchDesired()
    {
        await _store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");

        var replicas = new ReplicaRegistry();
        var nodes = new[]
        {
            NewNodeSnapshot("conn-a", "node-a"),
            NewNodeSnapshot("conn-b", "node-b")
        };
        replicas.Add("docs", "conn-a");
        replicas.Add("docs", "conn-b");

        var collections = await _store.ListCollectionsAsync();
        var block = StatusEndpoint.BuildVectorBlock(collections, replicas, nodes, replicationFactor: 2);

        var docs = Assert.Single(block.Collections);
        Assert.Equal(2, docs.LiveReplicas);
        Assert.False(docs.UnderReplicated);
        Assert.Equal(new[] { "node-a", "node-b" }, docs.ReplicaNodes);
    }

    private static NodeSnapshot NewNodeSnapshot(string connectionId, string nodeId)
    {
        return new NodeSnapshot(
            connectionId,
            nodeId,
            nodeId,
            "http://x",
            "test",
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            0,
            new Dictionary<string, string>(),
            null,
            false);
    }
}
