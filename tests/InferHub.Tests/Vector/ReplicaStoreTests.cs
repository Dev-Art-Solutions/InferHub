using InferHub.Node.Configuration;
using InferHub.Node.Vector;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class ReplicaStoreTests : IDisposable
{
    private readonly string _root;

    public ReplicaStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-replica-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AssignmentMaterialisesIndexAndQueriesReturnMatches()
    {
        using var store = NewStore();
        store.Apply(Assignment("docs", dimension: 2, distance: "cosine",
            seq: 2,
            new VectorRecord("a", [1f, 0f], null, null, 1, DateTimeOffset.UtcNow),
            new VectorRecord("b", [0f, 1f], null, null, 2, DateTimeOffset.UtcNow)));

        var matches = store.Query(new VectorQueryRequest("docs", [1f, 0f], K: 2, Filter: null));

        Assert.NotNull(matches);
        Assert.Equal("a", matches![0].Id);
    }

    [Fact]
    public void OpsAppliedAfterAssignmentAdvanceLastSeq()
    {
        using var store = NewStore();
        store.Apply(Assignment("docs", 2, "cosine", seq: 1,
            new VectorRecord("a", [1f, 0f], null, null, 1, DateTimeOffset.UtcNow)));

        store.Apply(new VectorReplicaOp("docs", "upsert", "b", [0f, 1f], null, null, 2, DateTimeOffset.UtcNow));
        store.Apply(new VectorReplicaOp("docs", "delete", "a", null, null, null, 3, DateTimeOffset.UtcNow));

        var matches = store.Query(new VectorQueryRequest("docs", [0f, 1f], K: 5, Filter: null));

        Assert.NotNull(matches);
        Assert.Single(matches!);
        Assert.Equal("b", matches![0].Id);
    }

    [Fact]
    public void StaleOpsBelowAssignedSeqAreIgnored()
    {
        using var store = NewStore();
        store.Apply(Assignment("docs", 2, "cosine", seq: 10,
            new VectorRecord("a", [1f, 0f], null, null, 5, DateTimeOffset.UtcNow)));

        // The assignment already covers seq <= 10, so a seq=3 op must be a no-op.
        store.Apply(new VectorReplicaOp("docs", "delete", "a", null, null, null, 3, DateTimeOffset.UtcNow));

        var matches = store.Query(new VectorQueryRequest("docs", [1f, 0f], K: 1, Filter: null));
        Assert.NotNull(matches);
        Assert.Single(matches!);
    }

    [Fact]
    public void RestartLoadsExistingReplicasWithoutRepush()
    {
        using (var store = NewStore())
        {
            store.Apply(Assignment("docs", 2, "cosine", seq: 2,
                new VectorRecord("a", [1f, 0f], null, null, 1, DateTimeOffset.UtcNow),
                new VectorRecord("b", [0f, 1f], null, null, 2, DateTimeOffset.UtcNow)));
            store.Apply(new VectorReplicaOp("docs", "upsert", "c", [0.9f, 0.1f], null, null, 3, DateTimeOffset.UtcNow));
        }

        using var reopened = NewStore();
        var matches = reopened.Query(new VectorQueryRequest("docs", [1f, 0f], K: 3, Filter: null));

        Assert.NotNull(matches);
        Assert.Equal(3, matches!.Count);
        Assert.Equal(new[] { "a", "c", "b" }, matches.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void DropRemovesReplicaAndBackingFiles()
    {
        using var store = NewStore();
        store.Apply(Assignment("docs", 2, "cosine", seq: 1,
            new VectorRecord("a", [1f, 0f], null, null, 1, DateTimeOffset.UtcNow)));

        store.Drop("docs");

        Assert.Null(store.Query(new VectorQueryRequest("docs", [1f, 0f], K: 1, Filter: null)));
        Assert.False(Directory.Exists(Path.Combine(_root, "docs")));
    }

    [Fact]
    public void QueryReturnsNullForUnknownCollection()
    {
        using var store = NewStore();

        var matches = store.Query(new VectorQueryRequest("missing", [1f, 0f], K: 1, Filter: null));

        Assert.Null(matches);
    }

    private ReplicaStore NewStore()
    {
        var opts = Options.Create(new VectorReplicaOptions { ReplicaDirectory = _root });
        return new ReplicaStore(opts, NullLogger<ReplicaStore>.Instance);
    }

    private static VectorReplicaAssignment Assignment(
        string name, int dimension, string distance, long seq, params VectorRecord[] records)
    {
        return new VectorReplicaAssignment(name, dimension, distance, records, seq);
    }
}
