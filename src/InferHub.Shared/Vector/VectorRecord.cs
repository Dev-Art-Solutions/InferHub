using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Vector;

public sealed record VectorRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("seqNo")] long SeqNo,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

public sealed record VectorUpsert(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("payload")] JsonElement? Payload = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null);
