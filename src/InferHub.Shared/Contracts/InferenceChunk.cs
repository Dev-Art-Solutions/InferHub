using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record InferenceChunk(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("responseJson")] string ResponseJson,
    [property: JsonPropertyName("done")] bool Done);
