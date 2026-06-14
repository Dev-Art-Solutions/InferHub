using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record InferenceJob(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("requestJson")] string RequestJson);
