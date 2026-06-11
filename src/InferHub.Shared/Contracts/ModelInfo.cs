using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record ModelInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("size")] long? SizeBytes);
