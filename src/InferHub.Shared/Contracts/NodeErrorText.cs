using System.Text.Json;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Turns a backend's error into the one sentence a human can act on.
/// </summary>
/// <remarks>
/// <para>
/// Ollama stuffs its own backend's JSON error into its <c>error</c> field as a *string*, so a
/// llama.cpp refusal arrives already double-encoded and lands in a client envelope triple-escaped:
/// an SDK reads <c>error.message</c> and shows the user a wall of backslashes instead of the one
/// sentence that says what to fix. Found live in phase 29, where "this model does not support
/// multimodal requests" is the error a vision user hits first.
/// </para>
/// <para>
/// This is <strong>presentation only</strong>: nothing is inferred from the text and no status code
/// is decided here. Unwrapping is not the same as interpreting — do not grow this into a function
/// that decides what an upstream error <em>means</em>, which would be a capability registry by the
/// back door (phase-29 D5).
/// </para>
/// <para>
/// It lives in <c>InferHub.Shared</c> since phase 37 because both hosts need it: the coordinator
/// unwraps a node's error, and a solo node unwraps its backend's — and solo is the deployment
/// <em>most</em> likely to surface a raw one, since there is no hub between the user and Ollama.
/// </para>
/// </remarks>
public static class NodeErrorText
{
    public const string Fallback = "node failed to run inference";

    public static string Readable(string? error)
    {
        var message = error;

        // Bounded: Ollama + llama.cpp produce two levels, and an unbounded loop over
        // caller-influenced text is not something to leave lying around.
        for (var depth = 0; depth < 4; depth++)
        {
            if (message is null || message.TrimStart() is not ['{', ..] trimmed)
            {
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);

                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var inner))
                {
                    break;
                }

                if (inner.ValueKind == JsonValueKind.String)
                {
                    message = inner.GetString();
                    continue;
                }

                if (inner.ValueKind == JsonValueKind.Object
                    && inner.TryGetProperty("message", out var sentence)
                    && sentence.ValueKind == JsonValueKind.String)
                {
                    message = sentence.GetString();
                    continue;
                }

                break;
            }
            catch (JsonException)
            {
                // Not JSON after all — what we already have is the best available text.
                break;
            }
        }

        return string.IsNullOrWhiteSpace(message) ? Fallback : message;
    }
}
