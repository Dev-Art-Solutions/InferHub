using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.Gemini;

/// <summary>
/// Google's standard error envelope, which is neither OpenAI's nor Anthropic's: one <c>error</c>
/// object carrying a <b>numeric</b> <c>code</c>, a <c>message</c>, a canonical <c>status</c> and a
/// heterogeneous <c>details</c> array.
/// </summary>
/// <remarks>
/// <b>The numeric <c>code</c> is the shape that broke the OpenAI dialect for eight releases</b>
/// until 62 found it — <c>error.code</c> is a string at OpenAI and a number at OpenRouter, the
/// envelope threw, and the raw body came back instead. It is typed as a number here from the first
/// line rather than discovered a second time (64 D9).
/// </remarks>
public sealed record GeminiErrorEnvelope(
    [property: JsonPropertyName("error")] GeminiErrorBody? Error);

/// <summary>
/// <c>status</c> is the canonical name — <c>INVALID_ARGUMENT</c>, <c>PERMISSION_DENIED</c>,
/// <c>RESOURCE_EXHAUSTED</c>, <c>UNAVAILABLE</c> — and it is the half of the answer an HTTP number
/// does not give you: a 400 that is <c>FAILED_PRECONDITION</c> is a billing problem and a 400 that
/// is <c>INVALID_ARGUMENT</c> is yours.
/// </summary>
public sealed record GeminiErrorBody(
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("details")] IReadOnlyList<JsonElement>? Details)
{
    private const string RetryInfoType = "type.googleapis.com/google.rpc.RetryInfo";

    /// <summary>
    /// The <c>retryDelay</c> Google puts in a <c>RetryInfo</c> detail on a 429, or null. This is
    /// Gemini's equivalent of the <c>request_id</c> 63 carried through: the operator-actionable
    /// field that a compatibility layer drops on the floor.
    /// </summary>
    /// <remarks>
    /// <c>details</c> is a list of arbitrary typed messages — <c>QuotaFailure</c>, <c>Help</c>,
    /// <c>ErrorInfo</c> — so it is read as raw JSON and scanned for the one entry worth surfacing,
    /// rather than modelled. Modelling all of them would be a schema this hub has no use for, and
    /// an unknown <c>@type</c> would then have to fail or be ignored, which is a decision about
    /// somebody else's roadmap.
    /// </remarks>
    public string? RetryDelay()
    {
        foreach (var detail in Details ?? [])
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!detail.TryGetProperty("@type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != RetryInfoType)
            {
                continue;
            }

            if (detail.TryGetProperty("retryDelay", out var delay)
                && delay.ValueKind == JsonValueKind.String
                && delay.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>Gemini answered, and it answered badly. Carries the status it used.</summary>
public sealed class GeminiUpstreamException(int statusCode, string message)
    : UpstreamDialectException(statusCode, message);
