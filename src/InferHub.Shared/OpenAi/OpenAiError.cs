using System.Text.Json.Serialization;

namespace InferHub.Shared.OpenAi;

/// <summary>
/// The OpenAI error envelope. SDKs read <c>error.message</c> to build the exception they
/// raise; an Ollama-shaped <c>{ "error": "..." }</c> body surfaces to the caller as an
/// unhelpful "unknown error", which is why <c>/v1</c> never uses it.
/// </summary>
public sealed record OpenAiErrorEnvelope(
    [property: JsonPropertyName("error")] OpenAiErrorBody Error)
{
    public static OpenAiErrorEnvelope Create(string message, string type, string? code = null, string? param = null)
        => new(new OpenAiErrorBody(message, type, param, code));
}

public sealed record OpenAiErrorBody(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("param")] string? Param,
    [property: JsonPropertyName("code")] string? Code);

public static class OpenAiErrorTypes
{
    public const string InvalidRequest = "invalid_request_error";
    public const string NotFound = "not_found_error";
    public const string ApiError = "api_error";
}
