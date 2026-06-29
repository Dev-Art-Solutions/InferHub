using System.Text.Json.Serialization;

namespace InferHub.Shared.Vector;

public sealed record CollectionInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("distance")] string Distance,
    [property: JsonPropertyName("recordCount")] long RecordCount,
    [property: JsonPropertyName("operations")] long Operations);

public sealed record CollectionPlacement(
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("targetReplicas")] int TargetReplicas,
    [property: JsonPropertyName("liveReplicas")] int LiveReplicas,
    [property: JsonPropertyName("replicaNodes")] IReadOnlyList<string> ReplicaNodes);

public sealed record CreateCollectionRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("distance")] string? Distance = null);
