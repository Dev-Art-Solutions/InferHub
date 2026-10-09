using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

public sealed record InferenceResult(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("responseJson")] string? ResponseJson,
    [property: JsonPropertyName("error")] string? Error,
    /// <summary>
    /// Phase 100: the HTTP status of a failure the node knows is the caller's — a 4xx, such as a picture
    /// sent to a model without an image encoder. Null is "the node or its engine failed", the 502 every
    /// failure was before v3.65 (and still is from an older node).
    /// </summary>
    [property: JsonPropertyName("status")] int? Status = null)
{
    /// <summary>The status an edge answers a failed job with: the node's 4xx when it gave one, else 502.</summary>
    public static int HttpStatusOf(int? status) => status is >= 400 and <= 499 ? status.Value : 502;

    /// <summary>A failure that is the request's, with its 4xx (phase 100).</summary>
    public static InferenceResult Refused(Guid jobId, string error, int status)
    {
        return new InferenceResult(jobId, false, null, error, status);
    }

    public static InferenceResult Succeeded(Guid jobId, string responseJson)
    {
        return new InferenceResult(jobId, true, responseJson, null);
    }

    public static InferenceResult Failed(Guid jobId, string error)
    {
        return new InferenceResult(jobId, false, null, error);
    }
}
