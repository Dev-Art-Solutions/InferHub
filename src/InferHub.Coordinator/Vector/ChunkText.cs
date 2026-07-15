using System.Text.Json;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Pulls the human-readable text out of a chunk's payload — the same <c>text</c>/<c>content</c>
/// convention the retrieval context block reads, kept in one place so the keyword index and the
/// prompt assembler can never disagree about what a chunk "says". A record written straight
/// through <c>/api/vector</c> with no text field simply has nothing to index for keyword search,
/// which is honest: BM25 over an opaque blob would be noise.
/// </summary>
internal static class ChunkText
{
    public static string Extract(JsonElement? payload)
    {
        if (payload is not { } value)
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
            if (value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
