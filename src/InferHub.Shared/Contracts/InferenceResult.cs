using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record InferenceResult(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("responseJson")] string? ResponseJson,
    [property: JsonPropertyName("error")] string? Error)
{
    public static InferenceResult Succeeded(Guid jobId, string responseJson)
    {
        return new InferenceResult(jobId, true, responseJson, null);
    }

    public static InferenceResult Failed(Guid jobId, string error)
    {
        return new InferenceResult(jobId, false, null, error);
    }
}
