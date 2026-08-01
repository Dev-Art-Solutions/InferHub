using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>The terminal answer for a <see cref="ToolJob"/>. Mirrors <see cref="InferenceResult"/>.</summary>
public sealed record ToolResult(
    [property: JsonPropertyName("jobId")] Guid JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("payload")] string? Payload,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("attachments")] IReadOnlyList<ToolAttachment>? Attachments = null,
    [property: JsonPropertyName("retryAfterSeconds")] int? RetryAfterSeconds = null,
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null)
{
    public static ToolResult Succeeded(
        Guid jobId,
        string? payload,
        IReadOnlyList<ToolAttachment>? attachments = null)
        => new(jobId, true, payload, null, attachments);

    /// <summary>
    /// A tool failure is a failed <em>job</em>, never a failed node (phase 41, D6) — which is why
    /// this exists at all rather than the executor being allowed to throw past the connection.
    /// </summary>
    public static ToolResult Failed(Guid jobId, string error)
        => new(jobId, false, null, error);

    /// <summary>
    /// A failure the worker attributed to the request itself (phase 42): an unproducible format, a
    /// voice that does not exist. Same reasoning as <see cref="Retry"/> one paragraph down — the
    /// node states which <em>kind</em> of failure it was and the edge renders a status from it,
    /// rather than the edge reading the message and guessing.
    /// </summary>
    public static ToolResult Refused(Guid jobId, string error, string? errorCode)
        => new(jobId, false, null, error, null, null, errorCode);

    /// <summary>
    /// A failure the caller should try again — every worker busy, or a tool that is temporarily not
    /// running. It exists because the alternative is the edge <em>sniffing the error text</em> to
    /// decide between a 502 and a 503, which is exactly the inference phase-29 D6 refuses to make:
    /// unwrap an upstream error, never interpret it. The node states the fact; the edge renders it.
    /// </summary>
    public static ToolResult Retry(Guid jobId, string error, int retryAfterSeconds)
        => new(jobId, false, null, error, null, Math.Max(1, retryAfterSeconds));
}
