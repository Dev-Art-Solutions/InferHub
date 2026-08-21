using System.Text.Json;
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
    [property: JsonPropertyName("code")]
    [property: JsonConverter(typeof(LenientErrorCodeConverter))] string? Code);

/// <summary>
/// Reads <c>code</c> whether the server wrote a string or a number (phase 62). OpenAI writes
/// <c>"rate_limit_exceeded"</c>; OpenRouter writes <c>429</c>.
/// </summary>
/// <remarks>
/// Without this the deserialization of the whole envelope throws, <c>Describe</c> catches its own
/// exception and falls back to the raw body — so the one sentence saying what to fix reaches the
/// operator buried in the JSON it arrived in, which is 29 D6's wall of backslashes by another
/// route. <b>Considered and rejected: a second error envelope for the servers that do this</b> —
/// the field is spelled <c>code</c> in both and means the same thing; what differs is a JSON scalar
/// type, which is not a schema. Writing is unchanged: a code this project produces is a string.
/// </remarks>
internal sealed class LenientErrorCodeConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var code)
                ? code.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            // Anything else is a server disagreeing about more than a scalar type. Skipping it
            // keeps the *message* readable, which is the only field a caller acts on.
            _ => SkipAndReturnNull(ref reader)
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value);
    }

    private static string? SkipAndReturnNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

public static class OpenAiErrorTypes
{
    public const string InvalidRequest = "invalid_request_error";
    public const string NotFound = "not_found_error";
    public const string ApiError = "api_error";
    public const string RateLimit = "rate_limit_error";
}
