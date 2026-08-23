using System.Text.Json;
using InferHub.Shared.Ollama;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.Gemini;

/// <summary>
/// Ollama in, Gemini out on the way to <c>:generateContent</c>; Gemini in, Ollama out on the way
/// back. Phase 64. The third of these, and it exists for the reason the first two did: rule 6 says
/// the mesh's internals know one wire format, so a new upstream dialect is a translation at the
/// boundary and nothing more.
/// </summary>
public static class GeminiTranslator
{
    private const string AssistantRole = "assistant";
    private const string SystemRole = "system";

    /// <summary>Gemini's two roles. <c>assistant</c> is <c>model</c> here and nowhere else.</summary>
    private const string GeminiModelRole = "model";

    private const string GeminiUserRole = "user";

    // Ollama's own two done_reason values, spelled here rather than borrowed from another dialect's
    // constants: they are Ollama's vocabulary, and one dialect reaching into another for a string
    // literal is the coupling this file exists to avoid.
    private const string StopReason = "stop";
    private const string LengthReason = "length";

    /// <summary>Gemini's name for "you hit the ceiling", which is Ollama's <c>length</c>.</summary>
    private const string MaxTokensFinishReason = "MAX_TOKENS";

    private const string ModelPathPrefix = "models/";

    // ---- 64 D2: the model id is a path segment ---------------------------------------

    /// <summary>
    /// The model as it must appear in the URL. <b>One rule for three forms:</b> an id that already
    /// contains a <c>/</c> is a path and is used as written; a bare id gets the <c>models/</c>
    /// prefix.
    /// </summary>
    /// <remarks>
    /// That accepts <c>gemini-3-pro</c> as typed, <c>models/gemini-3-pro</c> as the vendor's own
    /// listing hands it back, and <c>publishers/google/models/…</c> as a Vertex <c>BaseUrl</c>
    /// override needs. <b>Considered and rejected: a shape check</b> — 63 D8's argument unchanged,
    /// and this is why 64 normalizes where 62 D5 validates: the id is structural here, and getting
    /// it wrong produces a 404 naming a model the operator never typed
    /// (<c>models/models/gemini-3-pro</c>).
    /// <para>
    /// Each segment is escaped individually so a legitimate path keeps its separators while
    /// anything odd inside a segment cannot rewrite the URL.
    /// </para>
    /// </remarks>
    public static string ToModelPath(string? model)
    {
        var trimmed = (model ?? string.Empty).Trim().Trim('/');

        if (trimmed.Length == 0)
        {
            throw new GeminiUpstreamException(400, "a Gemini request named no model");
        }

        var escaped = string.Join('/', trimmed.Split('/').Select(Uri.EscapeDataString));

        return trimmed.Contains('/') ? escaped : ModelPathPrefix + escaped;
    }

    // ---- Ollama request → Gemini request ---------------------------------------------

    public static GeminiGenerateRequest ToGeminiChat(ChatRequest ollama, int? thinkingBudget)
    {
        var contents = new List<GeminiContent>();
        var system = new List<string>();

        foreach (var message in ollama.Messages ?? [])
        {
            // Gemini's contents array has exactly two roles, so a system turn is lifted into
            // systemInstruction — the same move 63 D3 made for Anthropic, onto a Content rather
            // than a string.
            if (string.Equals(message.Role, SystemRole, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    system.Add(message.Content!.Trim());
                }

                continue;
            }

            contents.Add(new GeminiContent
            {
                Role = string.Equals(message.Role, AssistantRole, StringComparison.OrdinalIgnoreCase)
                    ? GeminiModelRole
                    : GeminiUserRole,
                Parts = ToParts(message)
            });
        }

        return new GeminiGenerateRequest
        {
            Contents = contents,
            SystemInstruction = system.Count == 0
                ? null
                : new GeminiContent { Parts = [new GeminiPart { Text = string.Join("\n\n", system) }] },
            GenerationConfig = ToGenerationConfig(ollama.Options, thinkingBudget)
        };
    }

    /// <summary>
    /// <c>/api/generate</c> against a vendor that has no completions endpoint: the prompt becomes a
    /// single user turn. Ollama's own <c>system</c> field rides in the extension data and lands in
    /// <c>systemInstruction</c> with every other one.
    /// </summary>
    public static GeminiGenerateRequest ToGeminiGenerate(GenerateRequest ollama, int? thinkingBudget)
    {
        var system = ReadString(ollama.AdditionalProperties, SystemRole);

        return new GeminiGenerateRequest
        {
            Contents =
            [
                new GeminiContent
                {
                    Role = GeminiUserRole,
                    Parts = [new GeminiPart { Text = ollama.Prompt ?? string.Empty }]
                }
            ],
            SystemInstruction = system is null
                ? null
                : new GeminiContent { Parts = [new GeminiPart { Text = system }] },
            GenerationConfig = ToGenerationConfig(ollama.Options, thinkingBudget)
        };
    }

    /// <summary>
    /// One <c>:batchEmbedContents</c> body, whether the caller sent one input or forty (64 D8).
    /// <paramref name="modelPath"/> is repeated into each sub-request because the vendor requires it
    /// there as well as in the URL.
    /// </summary>
    public static GeminiBatchEmbedRequest ToGeminiEmbed(EmbedRequest ollama, string modelPath)
    {
        var inputs = ollama.Input.ValueKind switch
        {
            JsonValueKind.String => [ollama.Input.GetString() ?? string.Empty],
            JsonValueKind.Array => ollama.Input.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray(),
            _ => Array.Empty<string>()
        };

        return new GeminiBatchEmbedRequest
        {
            Requests = inputs
                .Select(text => new GeminiEmbedRequest
                {
                    Model = modelPath,
                    Content = new GeminiContent { Parts = [new GeminiPart { Text = text }] }
                })
                .ToArray()
        };
    }

    // ---- Gemini response → Ollama response -------------------------------------------

    public static ChatResponse ToOllamaChat(GeminiGenerateResponse response, string requestedModel)
    {
        var candidate = Candidate(response);

        var ollama = new ChatResponse
        {
            Model = response.ModelVersion ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage { Role = AssistantRole, Content = TextOf(candidate) },
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], candidate?.FinishReason);
        ApplyUsage(ollama, response.UsageMetadata);

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

    public static ChatResponse ToOllamaChatDone(string model, string? finishReason, GeminiUsageMetadata? usage)
    {
        var ollama = new ChatResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage { Role = AssistantRole, Content = string.Empty },
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], finishReason);
        ApplyUsage(ollama, usage);

        return ollama;
    }

    public static GenerateResponse ToOllamaGenerate(GeminiGenerateResponse response, string requestedModel)
    {
        var candidate = Candidate(response);

        var ollama = new GenerateResponse
        {
            Model = response.ModelVersion ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = TextOf(candidate),
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], candidate?.FinishReason);
        ApplyUsage(ollama, response.UsageMetadata);

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

    public static GenerateResponse ToOllamaGenerateDone(string model, string? finishReason, GeminiUsageMetadata? usage)
    {
        var ollama = new GenerateResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = string.Empty,
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], finishReason);
        ApplyUsage(ollama, usage);

        return ollama;
    }

    public static EmbedResponse ToOllamaEmbed(GeminiBatchEmbedResponse response, string requestedModel)
        => new()
        {
            Model = requestedModel,
            Embeddings = (response.Embeddings ?? [])
                .Select(embedding => (embedding.Values ?? []).ToList())
                .ToList()
        };

    /// <summary>
    /// The text of one streamed chunk, or empty. A chunk with only a <c>finishReason</c> on it is
    /// normal and must not become an empty delta a client has to filter.
    /// </summary>
    public static string TextOf(GeminiGenerateResponse response) => TextOf(Candidate(response));

    /// <summary>
    /// The reason the prompt was refused before the model saw it, or null (64 D7). A response
    /// carrying this and no candidates is the vendor's way of saying no with a 200.
    /// </summary>
    public static string? BlockReason(GeminiGenerateResponse response)
        => response.Candidates is { Count: > 0 }
            ? null
            : response.PromptFeedback?.BlockReason;

    // ---- helpers ----------------------------------------------------------------------

    private static GeminiCandidate? Candidate(GeminiGenerateResponse response)
        => response.Candidates is { Count: > 0 } candidates ? candidates[0] : null;

    /// <summary>
    /// Every text part, concatenated, with thought summaries skipped. A candidate with no text is
    /// an empty answer, not a null one.
    /// </summary>
    private static string TextOf(GeminiCandidate? candidate)
        => string.Concat((candidate?.Content?.Parts ?? [])
            .Where(part => part.Thought != true && part.Text is not null)
            .Select(part => part.Text));

    /// <summary>
    /// A text part, plus one <c>inlineData</c> part per Ollama image, over the same magic-byte
    /// sniff the other two dialects use — Gemini wants the media type as its own field, which is
    /// the only difference from Anthropic's source block.
    /// </summary>
    private static IReadOnlyList<GeminiPart> ToParts(ChatMessage message)
    {
        var text = message.Content ?? string.Empty;

        if (message.Images is not { ValueKind: JsonValueKind.Array } images || images.GetArrayLength() == 0)
        {
            return [new GeminiPart { Text = text }];
        }

        var parts = new List<GeminiPart>();

        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.String || image.GetString() is not { Length: > 0 } base64)
            {
                continue;
            }

            string mimeType;

            try
            {
                mimeType = Base64MediaType.Sniff(base64);
            }
            catch (Base64MediaTypeException ex)
            {
                throw new GeminiUpstreamException(400, ex.Message);
            }

            parts.Add(new GeminiPart { InlineData = new GeminiInlineData { MimeType = mimeType, Data = base64 } });
        }

        // The text part goes last, matching 63's ordering and the vendor's own examples: the
        // question reads better after the thing it is about.
        parts.Add(new GeminiPart { Text = text });

        return parts;
    }

    /// <summary>
    /// <b>Nothing is dropped here</b>, which is the contrast worth noticing with 63 D4: Gemini's
    /// <c>generationConfig</c> has <c>seed</c>, <c>presencePenalty</c> and <c>frequencyPenalty</c>,
    /// so Anthropic's drop was a fact about that vendor and not a house policy.
    /// </summary>
    /// <remarks>
    /// <c>maxOutputTokens</c> travels only when a caller named <c>num_predict</c> (64 D6): the
    /// vendor does not require it, and a declared default would be a ceiling nobody asked for.
    /// </remarks>
    private static GeminiGenerationConfig? ToGenerationConfig(JsonElement? options, int? thinkingBudget)
    {
        var config = new GeminiGenerationConfig
        {
            ThinkingConfig = thinkingBudget is { } budget
                ? new GeminiThinkingConfig { ThinkingBudget = budget }
                : null
        };

        if (options is { ValueKind: JsonValueKind.Object } element)
        {
            config.Temperature = ReadDouble(element, "temperature");
            config.TopP = ReadDouble(element, "top_p");
            config.TopK = ReadInt(element, "top_k");
            config.Seed = ReadInt(element, "seed");
            config.PresencePenalty = ReadDouble(element, "presence_penalty");
            config.FrequencyPenalty = ReadDouble(element, "frequency_penalty");

            if (ReadInt(element, "num_predict") is { } predict && predict > 0)
            {
                config.MaxOutputTokens = predict;
            }

            if (element.TryGetProperty("stop", out var stop))
            {
                config.StopSequences = stop.ValueKind switch
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

        // An empty generationConfig is legal and pointless. Sending null keeps a request that set
        // nothing byte-identical to the vendor's own minimal example, which is what a captured
        // payload in the tests is compared against.
        return IsEmpty(config) ? null : config;
    }

    private static bool IsEmpty(GeminiGenerationConfig config)
        => config is
        {
            Temperature: null, TopP: null, TopK: null, MaxOutputTokens: null,
            StopSequences: null, Seed: null, PresencePenalty: null,
            FrequencyPenalty: null, ThinkingConfig: null
        };

    private static void ApplyDone(Dictionary<string, JsonElement> extras, string? finishReason)
    {
        // Ollama knows "stop" and "length". SAFETY, RECITATION and whatever Google adds next have
        // no Ollama spelling and are not invented one here — the answer itself is what a client
        // reads, and a *blocked* prompt never reaches this path at all (64 D7).
        var reason = finishReason == MaxTokensFinishReason ? LengthReason : StopReason;

        extras["done_reason"] = JsonSerializer.SerializeToElement(reason);
    }

    /// <summary>
    /// 64 D5 and D6. <c>promptTokenCount</c> already includes the cached tokens and is passed
    /// through whole; <c>thoughtsTokenCount</c> is billed as output and is still not folded into
    /// <c>eval_count</c>, because a client reading that field reads "tokens in the answer I
    /// received". A response with no usage block yields no counts, not zeros (v3.13.1's rule).
    /// </summary>
    private static void ApplyUsage(ChatResponse ollama, GeminiUsageMetadata? usage)
    {
        if (usage is null)
        {
            return;
        }

        ollama.PromptEvalCount = usage.PromptTokenCount;
        ollama.EvalCount = usage.CandidatesTokenCount;
    }

    private static void ApplyUsage(GenerateResponse ollama, GeminiUsageMetadata? usage)
    {
        if (usage is null)
        {
            return;
        }

        ollama.PromptEvalCount = usage.PromptTokenCount;
        ollama.EvalCount = usage.CandidatesTokenCount;
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
