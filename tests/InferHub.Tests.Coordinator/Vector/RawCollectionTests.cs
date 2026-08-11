using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Tests.Vector;

public class RawCollectionTests : IDisposable
{
    private readonly string _root;

    public RawCollectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inferhub-rawcoll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AppendAndReplayPreservesOpsOrder()
    {
        var raw = RawCollection.Create(_root, "docs", dimension: 3, distance: "cosine");
        raw.AppendUpsert(Record("a", [1f, 0f, 0f], seq: 1));
        raw.AppendUpsert(Record("b", [0f, 1f, 0f], seq: 2));
        raw.AppendDelete("a", seqNo: 3, timestamp: DateTimeOffset.UtcNow);
        raw.Close();

        var reopened = RawCollection.Open(Path.Combine(_root, "docs"));
        var ops = reopened.Replay().ToList();

        Assert.Equal(3, ops.Count);
        Assert.Equal(new[] { "upsert", "upsert", "delete" }, ops.Select(o => o.Op).ToArray());
        Assert.Equal(new[] { 1L, 2L, 3L }, ops.Select(o => o.SeqNo).ToArray());
    }

    [Fact]
    public void SnapshotIsFoldedIntoReplay()
    {
        var raw = RawCollection.Create(_root, "docs", dimension: 2, distance: "dot");
        raw.AppendUpsert(Record("a", [1f, 0f], seq: 1));
        raw.AppendUpsert(Record("b", [0f, 1f], seq: 2));
        raw.AppendUpsert(Record("a", [9f, 9f], seq: 3));

        raw.WriteSnapshot(
            liveRecords: new[]
            {
                Record("a", [9f, 9f], seq: 3),
                Record("b", [0f, 1f], seq: 2)
            },
            snapshotSeq: 3);

        // After the snapshot the ops tail is empty; replay should come purely from the snapshot.
        var reopened = RawCollection.Open(Path.Combine(_root, "docs"));
        var ops = reopened.Replay().ToList();

        Assert.Equal(2, ops.Count);
        Assert.All(ops, o => Assert.Equal("upsert", o.Op));
        Assert.Contains(ops, o => o.Id == "a" && o.Vector![0] == 9f);
    }

    [Fact]
    public void ReplaySkipsOpsAlreadyCoveredBySnapshot()
    {
        // Simulate a crash between snapshot rename and ops-truncate: snapshot covers
        // seq <= 2, but the ops file still has the seq=1 and seq=2 entries on disk.
        var collectionDir = Path.Combine(_root, "docs");
        Directory.CreateDirectory(collectionDir);

        var raw = RawCollection.Create(_root, "docs", dimension: 2, distance: "dot");
        raw.AppendUpsert(Record("a", [1f, 0f], seq: 1));
        raw.AppendUpsert(Record("b", [0f, 1f], seq: 2));
        raw.Close();

        // Write a snapshot covering both records but leave the ops file untouched on disk
        // by directly manipulating the filesystem.
        File.WriteAllText(Path.Combine(collectionDir, "snapshot.jsonl"),
            "{\"op\":\"upsert\",\"id\":\"a\",\"vector\":[1.0,0.0],\"seqNo\":1,\"timestampUtc\":\"2026-06-28T00:00:00+00:00\"}\n" +
            "{\"op\":\"upsert\",\"id\":\"b\",\"vector\":[0.0,1.0],\"seqNo\":2,\"timestampUtc\":\"2026-06-28T00:00:00+00:00\"}\n");
        File.WriteAllText(Path.Combine(collectionDir, "snapshot.seq"), "2");

        var reopened = RawCollection.Open(collectionDir);
        var ops = reopened.Replay().ToList();

        // Should yield 2 entries from the snapshot, not 4 (no double-application of the
        // stale ops file).
        Assert.Equal(2, ops.Count);
    }

    [Fact]
    public void DropRemovesCollectionDirectory()
    {
        var raw = RawCollection.Create(_root, "docs", dimension: 2, distance: "cosine");
        raw.AppendUpsert(Record("a", [1f, 0f], seq: 1));

        raw.Drop();

        Assert.False(Directory.Exists(Path.Combine(_root, "docs")));
    }

    private static VectorRecord Record(string id, float[] vector, long seq) =>
        new(id, vector, Payload: null, Metadata: null, SeqNo: seq, TimestampUtc: DateTimeOffset.UtcNow);
}
