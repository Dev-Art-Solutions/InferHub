using System.Text;
using System.Text.Json.Nodes;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// Which of colibri's KV slots a request belongs on (93 D2): a pure function of the conversation's
/// <em>opening</em>, so every later turn — which re-sends that opening, rule 7 — lands on the same
/// slot with no table anywhere.
/// </summary>
/// <remarks>
/// A collision costs speed, never an answer: the engine matches each prompt against the slot's own
/// history and re-prefills whatever differs. That is the same property colibri holds itself to —
/// placement decides speed, not semantics — and it is why a stateless hash is enough.
/// </remarks>
public static class ColibriSlot
{
    /// <summary>A generate's opening is its first 512 characters: enough to separate documents, short enough that a growing prompt keeps its slot.</summary>
    public const int PromptOpeningChars = 512;

    /// <summary>Messages up to and including the first <c>user</c> one; all of them if there is none.</summary>
    public static int? ForChat(JsonObject request, int slots)
    {
        if (slots <= 1 || request["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return null;
        }

        var opening = new StringBuilder();

        foreach (var message in messages)
        {
            var role = message?["role"]?.GetValue<string>() ?? "";

            // Content is a string or an array of parts; its JSON text is stable across turns either way.
            opening.Append(role).Append('\u0000').Append(message?["content"]?.ToJsonString()).Append('\u0001');

            if (role == "user")
            {
                break;
            }
        }

        return Slot(opening.ToString(), slots);
    }

    public static int? ForCompletion(JsonObject request, int slots)
    {
        if (slots <= 1 || request["prompt"] is not JsonValue value || !value.TryGetValue<string>(out var prompt))
        {
            return null;
        }

        return Slot(prompt.Length <= PromptOpeningChars ? prompt : prompt[..PromptOpeningChars], slots);
    }

    /// <summary>FNV-1a over UTF-8: stable across processes and releases, unlike <c>string.GetHashCode</c>.</summary>
    internal static int Slot(string opening, int slots)
    {
        var hash = 2166136261u;

        foreach (var b in Encoding.UTF8.GetBytes(opening))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash % (uint)slots);
    }
}

/// <summary>
/// Everything a colibri node changes about a request on the wire, and it is two things.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every body goes with a <c>Content-Length</c> (93 D6, found against a real engine).</b>
/// <c>coli serve</c> is Python's <c>http.server</c> and reads exactly <c>Content-Length</c> bytes;
/// the shared client's <c>JsonContent</c> has no precomputed length and goes chunked, which the
/// gateway answers with <c>400 Request body must be between 1 and 4194304 bytes</c> — every chat,
/// every stream. vLLM, llama.cpp and the vendors all accept chunked, which is why no stub found it.
/// </para>
/// <para>
/// <b>A <c>cache_slot</c> when the engine keeps more than one</b> (93 D2). A handler rather than a
/// field on the shared OpenAI DTOs: both facts are colibri's, and rule 1 keeps a backend's
/// vocabulary inside <c>Backends/</c>.
/// </para>
/// </remarks>
public sealed class ColibriRequestHandler(int slots) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Post && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri?.AbsolutePath ?? "";
            var chat = path.EndsWith("/chat/completions", StringComparison.Ordinal);

            if (slots > 1
                && (chat || path.EndsWith("/completions", StringComparison.Ordinal))
                && JsonNode.Parse(body) is JsonObject json
                && !json.ContainsKey("cache_slot")
                && (chat ? ColibriSlot.ForChat(json, slots) : ColibriSlot.ForCompletion(json, slots)) is { } slot)
            {
                json["cache_slot"] = slot;
                body = json.ToJsonString();
            }

            // StringContent knows its length, so the request carries Content-Length, not chunks.
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
