using System.Text.Json.Serialization;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.Anthropic;

/// <summary>
/// Anthropic's error envelope, which is not OpenAI's: a top-level <c>type</c> of <c>"error"</c>, the
/// body under <c>error</c>, and a <c>request_id</c> beside them.
/// </summary>
/// <remarks>
/// The <c>request_id</c> is the thing Anthropic support asks for, and reaching an operator's log
/// without it means the one identifier that resolves a ticket was thrown away at the boundary. It
/// is an opaque id assigned by the vendor, not anything about the request, so rule 7 has no opinion
/// on carrying it. The same envelope arrives as an <c>event: error</c> frame mid-stream (63 D6).
/// </remarks>
public sealed record AnthropicErrorEnvelope(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("error")] AnthropicErrorBody? Error,
    [property: JsonPropertyName("request_id")] string? RequestId);

/// <summary><c>type</c> is <c>invalid_request_error</c>, <c>rate_limit_error</c>, <c>overloaded_error</c>, and it grows.</summary>
public sealed record AnthropicErrorBody(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("message")] string? Message);

/// <summary>Anthropic answered, and it answered badly. Carries the status it used.</summary>
public sealed class AnthropicUpstreamException(int statusCode, string message)
    : UpstreamDialectException(statusCode, message);
