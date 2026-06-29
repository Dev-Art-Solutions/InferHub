using System.Text.Json.Serialization;

namespace InferHub.Shared.Vector.Replication;

public sealed record VectorReplicaAssignment(
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("distance")] string Distance,
    [property: JsonPropertyName("records")] IReadOnlyList<VectorRecord> Records,
    [property: JsonPropertyName("lastSeq")] long LastSeq);

public sealed record VectorReplicaOp(
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[]? Vector,
    [property: JsonPropertyName("payload")] System.Text.Json.JsonElement? Payload,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("seqNo")] long SeqNo,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

public sealed record VectorQueryRequest(
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("k")] int K,
    [property: JsonPropertyName("filter")] IReadOnlyDictionary<string, string>? Filter);

public sealed record VectorQueryResponse(
    [property: JsonPropertyName("matches")] IReadOnlyList<VectorMatch> Matches);
