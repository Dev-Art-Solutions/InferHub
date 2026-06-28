using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Vector;

public sealed record VectorQuery(
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("k")] int K = 10,
    [property: JsonPropertyName("filter")] IReadOnlyDictionary<string, string>? Filter = null);

public sealed record VectorMatch(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);
