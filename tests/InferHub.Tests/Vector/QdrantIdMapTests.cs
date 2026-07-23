using System.Text.RegularExpressions;
using InferHub.Coordinator.Vector.Qdrant;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Tests.Vector;

/// <summary>
/// The id map is the one real gotcha of the Qdrant connector: Qdrant accepts only an int or a UUID
/// as a point id, so every InferHub id becomes a deterministic UUIDv5. Determinism keeps re-ingest
/// idempotent; distinctness keeps two ids from colliding onto one point. No server needed.
/// </summary>
public class QdrantIdMapTests
{
    private static readonly Regex Uuidv5 = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        RegexOptions.Compiled);

    [Fact]
    public void SameIdMapsToSamePointId()
    {
        var id = "sha256:abc123:0";
        Assert.Equal(QdrantIdMap.ToPointId(id), QdrantIdMap.ToPointId(id));
    }

    [Fact]
    public void OutputIsACanonicalUuidV5()
    {
        var pointId = QdrantIdMap.ToPointId("handbook.pdf:12");
        Assert.Matches(Uuidv5, pointId);
    }

    [Fact]
    public void DistinctIdsMapToDistinctPointIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var points = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 20_000; i++)
        {
            var id = $"doc-{i}:chunk-{i % 7}";
            ids.Add(id);
            points.Add(QdrantIdMap.ToPointId(id));
        }

        // No collisions: every distinct real id produced a distinct point id.
        Assert.Equal(ids.Count, points.Count);
    }

    [Theory]
    [InlineData(DistanceMetric.Cosine, "Cosine")]
    [InlineData(DistanceMetric.Dot, "Dot")]
    [InlineData(DistanceMetric.L2, "Euclid")]
    public void DistanceMapsToQdrantEnum(DistanceMetric metric, string expected)
    {
        Assert.Equal(expected, QdrantClient.ToQdrantDistance(metric));
    }
}
