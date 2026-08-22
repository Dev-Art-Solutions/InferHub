using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Ollama;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.Anthropic;

/// <summary>
/// Ollama in, Anthropic out on the way to <c>/v1/messages</c>; Anthropic in, Ollama out on the way
/// back. Phase 63. The mirror of <see cref="InferHub.Shared.OpenAi.UpstreamTranslator"/>, and it
/// exists for the same reason: rule 6 says the mesh's internals know one wire format, so a second
/// upstream dialect is a translation at the boundary and nothing more.
/// </summary>
public static class AnthropicTranslator
{
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";
    private const string SystemRole = "system";

    // Ollama's own two done_reason values. Spelled here rather than borrowed from the OpenAI
    // translator's constants: they are Ollama's vocabulary, and one dialect reaching into another
    // for a string literal is the coupling this file exists to avoid.
    private const string StopReason = "stop";
    private const string LengthReason = "length";

    /// <summary>Anthropic's own name for "you hit the ceiling", which is Ollama's <c>length</c>.</summary>
    private const string MaxTokensStopReason = "max_tokens";

    // ---- Ollama request → Anthropic request ------------------------------------------

    /// <summary>
    /// <paramref name="defaultMaxTokens"/> is the provider's declared ceiling (63 D2). A caller's
    /// <c>options.num_predict</c> always wins; the declared value is what makes a request legal at
    /// all, because Anthropic requires the field and Ollama has no equivalent to carry.
    /// </summary>
    public static AnthropicMessagesRequest ToAnthropicChat(ChatRequest ollama, int defaultMaxTokens)
    {
        var messages = new List<AnthropicMessage>();
        var system = new List<string>();

        foreach (var message in ollama.Messages ?? [])
        {
            // 63 D3. Anthropic's messages array has exactly two roles, so a system turn is lifted
            // rather than sent. The mid-conversation {"role":"system"} form exists on some models
            // and is a 400 on others, which would make this translation depend on which model the
            // operator happened to map.
            if (string.Equals(message.Role, SystemRole, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    system.Add(message.Content!.Trim());
                }

                continue;
            }

            messages.Add(new AnthropicMessage
            {
                Role = string.Equals(message.Role, AssistantRole, StringComparison.OrdinalIgnoreCase)
                    ? AssistantRole
                    : UserRole,
                Content = ToAnthropicContent(message)
            });
        }

        var request = new AnthropicMessagesRequest
        {
            Model = ollama.Model,
            Messages = messages,
            System = system.Count == 0 ? null : string.Join("\n\n", system),
            MaxTokens = defaultMaxTokens
        };

        ApplyOptions(ollama.Options, request, defaultMaxTokens);

        return request;
    }

    /// <summary>
    /// <c>/api/generate</c> against a vendor that has no completions endpoint: the prompt becomes a
    /// single user turn. Ollama's own <c>system</c> field rides in the extension data and lands
    /// where 63 D3 puts every other one.
    /// </summary>
    public static AnthropicMessagesRequest ToAnthropicGenerate(GenerateRequest ollama, int defaultMaxTokens)
    {
        var request = new AnthropicMessagesRequest
        {
            Model = ollama.Model,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = UserRole,
                    Content = JsonSerializer.SerializeToElement(ollama.Prompt ?? string.Empty)
                }
            ],
            System = ReadString(ollama.AdditionalProperties, SystemRole),
            MaxTokens = defaultMaxTokens
        };

        ApplyOptions(ollama.Options, request, defaultMaxTokens);

        return request;
    }

    // ---- Anthropic response → Ollama response ----------------------------------------

    public static ChatResponse ToOllamaChat(AnthropicMessageResponse response, string requestedModel)
    {
        var ollama = new ChatResponse
        {
            Model = response.Model ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage { Role = AssistantRole, Content = TextOf(response) },
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], response.StopReason);
        ApplyUsage(ollama, response.Usage);

        return ollama;
    }

    public static ChatResponse ToOllamaChatDelta(string text, string model)
        => new()
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage { Role = AssistantRole, Content = text },
            Done = false
        };

    /// <summary>
    /// The terminal chunk, emitted once the stream ends rather than the moment a stop reason
    /// arrives — Anthropic's counts are still moving in the <c>message_delta</c> that carries it.
    /// </summary>
    public static ChatResponse ToOllamaChatDone(string model, string? stopReason, AnthropicUsage? usage)
    {
        var ollama = new ChatResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage { Role = AssistantRole, Content = string.Empty },
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], stopReason);
        ApplyUsage(ollama, usage);

        return ollama;
    }

    public static GenerateResponse ToOllamaGenerate(AnthropicMessageResponse response, string requestedModel)
    {
        var ollama = new GenerateResponse
        {
            Model = response.Model ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = TextOf(response),
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], response.StopReason);
        ApplyUsage(ollama, response.Usage);

        return ollama;
    }

    public static GenerateResponse ToOllamaGenerateDelta(string text, string model)
        => new()
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = text,
            Done = false
        };

    public static GenerateResponse ToOllamaGenerateDone(string model, string? stopReason, AnthropicUsage? usage)
    {
        var ollama = new GenerateResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = string.Empty,
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], stopReason);
        ApplyUsage(ollama, usage);

        return ollama;
    }

    // ---- helpers ----------------------------------------------------------------------

    /// <summary>Every text block, concatenated. A response with none is an empty answer, not a null one.</summary>
    private static string TextOf(AnthropicMessageResponse response)
        => string.Concat((response.Content ?? [])
            .Where(block => block.Type == "text" && block.Text is not null)
            .Select(block => block.Text));

    /// <summary>
    /// A text-only turn stays a plain string. A turn carrying Ollama's <c>images</c> becomes
    /// Anthropic content blocks, over the same magic-byte sniff the OpenAI dialect uses
    /// (<see cref="Base64MediaType"/>) — Anthropic wants the media type as its own field rather
    /// than inside a data URL, which is the only difference.
    /// </summary>
    private static JsonElement ToAnthropicContent(ChatMessage message)
    {
        var text = message.Content ?? string.Empty;

        if (message.Images is not { ValueKind: JsonValueKind.Array } images || images.GetArrayLength() == 0)
        {
            return JsonSerializer.SerializeToElement(text);
        }

        var parts = new JsonArray();

        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.String || image.GetString() is not { Length: > 0 } base64)
            {
                continue;
            }

            string mediaType;

            try
            {
                mediaType = Base64MediaType.Sniff(base64);
            }
            catch (Base64MediaTypeException ex)
            {
                throw new AnthropicUpstreamException(400, ex.Message);
            }

            parts.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = mediaType,
                    ["data"] = base64
                }
            });
        }

        // The text block goes last: Anthropic's own guidance is that an image reads better with the
        // question after it, and the OpenAI dialect's order is a data-URL convention, not a rule.
        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });

        return JsonSerializer.SerializeToElement(parts);
    }

    /// <summary>
    /// 63 D4: what Anthropic has, translated; what it does not have, dropped. <c>seed</c>,
    /// <c>presence_penalty</c> and <c>frequency_penalty</c> have no counterpart, and Anthropic
    /// rejects an unknown top-level parameter with a 400 — so forwarding them would refuse every
    /// request that carried one.
    /// </summary>
    private static void ApplyOptions(JsonElement? options, AnthropicMessagesRequest request, int defaultMaxTokens)
    {
        if (options is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        request.Temperature = ReadDouble(element, "temperature");
        request.TopP = ReadDouble(element, "top_p");
        request.TopK = ReadInt(element, "top_k");
        request.MaxTokens = ReadInt(element, "num_predict") is { } predict && predict > 0
            ? predict
            : defaultMaxTokens;

        if (element.TryGetProperty("stop", out var stop))
        {
            request.StopSequences = stop.ValueKind switch
            {
                JsonValueKind.String => [stop.GetString()!],
                JsonValueKind.Array => stop.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString()!)
                    .ToArray(),
                _ => null
            };
        }
    }

    private static void ApplyDone(Dictionary<string, JsonElement> extras, string? stopReason)
    {
        // Ollama knows "stop" and "length". Anthropic's tool_use and refusal have no Ollama
        // spelling and are not invented one here — the answer itself is what a client reads.
        var reason = stopReason == MaxTokensStopReason ? LengthReason : StopReason;

        extras["done_reason"] = JsonSerializer.SerializeToElement(reason);
    }

    /// <summary>
    /// 63 D5. The cache counts are deliberately not folded into the prompt count, and a response
    /// with no usage block leaves both fields absent rather than zero (v3.13.1's rule).
    /// </summary>
    private static void ApplyUsage(ChatResponse ollama, AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return;
        }

        ollama.PromptEvalCount = usage.InputTokens;
        ollama.EvalCount = usage.OutputTokens;
    }

    private static void ApplyUsage(GenerateResponse ollama, AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return;
        }

        ollama.PromptEvalCount = usage.InputTokens;
        ollama.EvalCount = usage.OutputTokens;
    }

    private static string? ReadString(Dictionary<string, JsonElement>? extras, string name)
        => extras is not null
            && extras.TryGetValue(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
                ? text
                : null;

    private static double? ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
