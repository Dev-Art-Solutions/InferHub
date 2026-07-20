using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Ollama;

namespace InferHub.Shared.OpenAi;

/// <summary>
/// The mirror of <see cref="RequestTranslator"/> and <see cref="ResponseTranslator"/>: Ollama in,
/// OpenAI out on the way to an upstream server; OpenAI in, Ollama out on the way back. The node
/// drives this against vLLM, llama.cpp's server, LM Studio, TGI or a hosted provider.
/// </summary>
/// <remarks>
/// A request arriving on <c>/v1</c> is therefore translated to Ollama at the coordinator and back
/// to OpenAI at the node — two <c>JsonSerializer</c> round-trips on a call that is about to occupy
/// a GPU for seconds. The alternative was a polymorphic job payload carrying a dialect tag, which
/// would push dialect-awareness into the dispatcher, the router, the affinity keys and the
/// retrieval pipeline, and every test that touches them. The mesh's internal protocol stays one
/// shape (rule 6); the round-trips are the price, and they are cheap. Do not "fix" this.
/// </remarks>
public static class UpstreamTranslator
{
    private const string ObjectRole = "assistant";

    // Not `u8` literals: 0x89 and 0xFF are not ASCII, and a UTF-8 literal would encode them as
    // two bytes each — a signature that matches nothing.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    // ---- Ollama request → OpenAI request ---------------------------------------------

    public static ChatCompletionRequest ToOpenAiChat(ChatRequest ollama)
    {
        var request = new ChatCompletionRequest
        {
            Model = ollama.Model,
            Messages = (ollama.Messages ?? []).Select(ToOpenAiMessage).ToArray(),
            Stream = ollama.Stream ?? false
        };

        ApplyOptions(ollama.Options, request);

        if (ollama.AdditionalProperties is { } extras)
        {
            if (extras.TryGetValue("tools", out var tools))
            {
                request.Tools = tools;
            }

            if (extras.TryGetValue("tool_choice", out var toolChoice))
            {
                request.ToolChoice = toolChoice;
            }

            if (extras.TryGetValue("format", out var format))
            {
                request.ResponseFormat = ToResponseFormat(format);
            }
        }

        return request;
    }

    public static CompletionRequest ToOpenAiCompletion(GenerateRequest ollama)
    {
        var request = new CompletionRequest
        {
            Model = ollama.Model,
            Prompt = JsonSerializer.SerializeToElement(ollama.Prompt ?? string.Empty),
            Stream = ollama.Stream ?? false
        };

        ApplyOptions(ollama.Options, request);

        return request;
    }

    /// <summary>
    /// We control this call, so we ask for <c>float</c> and skip the base64 decode path
    /// entirely — the coordinator's own <c>/v1/embeddings</c> handler is the only place that
    /// has to care what the *client* asked for.
    /// </summary>
    public static OpenAiEmbeddingsRequest ToOpenAiEmbeddings(EmbedRequest ollama)
        => new()
        {
            Model = ollama.Model,
            Input = ollama.Input,
            EncodingFormat = "float"
        };

    // ---- OpenAI response → Ollama response -------------------------------------------

    public static ChatResponse ToOllamaChat(ChatCompletionResponse response, string requestedModel)
    {
        var choice = response.Choices?.FirstOrDefault();
        var message = choice?.Message;

        var ollama = new ChatResponse
        {
            Model = response.Model ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage
            {
                Role = message?.Role ?? ObjectRole,
                Content = message?.Content ?? string.Empty,
                AdditionalProperties = ToOllamaToolCalls(message?.ToolCalls)
            },
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], choice?.FinishReason);
        ApplyUsage(ollama, response.Usage);

        return ollama;
    }

    /// <summary>A mid-stream delta. Never terminal — see <see cref="ToOllamaChatDone"/>.</summary>
    public static ChatResponse ToOllamaChatDelta(ChatCompletionChunk chunk, string requestedModel)
    {
        var delta = chunk.Choices?.FirstOrDefault()?.Delta;

        return new ChatResponse
        {
            Model = chunk.Model ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage
            {
                Role = delta?.Role ?? ObjectRole,
                Content = delta?.Content ?? string.Empty
            },
            Done = false
        };
    }

    /// <summary>
    /// The terminal chunk. Emitted once, at the end of the upstream stream, rather than the
    /// moment <c>finish_reason</c> arrives: OpenAI sends token counts in a *later* usage-only
    /// chunk, and a done chunk without <c>eval_count</c> would silently cost phase 25 its meter.
    /// </summary>
    public static ChatResponse ToOllamaChatDone(string model, string? finishReason, OpenAiUsage? usage)
    {
        var ollama = new ChatResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Message = new ChatMessage { Role = ObjectRole, Content = string.Empty },
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], finishReason);
        ApplyUsage(ollama, usage);

        return ollama;
    }

    public static GenerateResponse ToOllamaGenerate(CompletionResponse response, string requestedModel)
    {
        var choice = response.Choices?.FirstOrDefault();

        var ollama = new GenerateResponse
        {
            Model = response.Model ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = choice?.Text ?? string.Empty,
            Done = true
        };

        ApplyDone(ollama.AdditionalProperties ??= [], choice?.FinishReason);
        ApplyUsage(ollama, response.Usage);

        return ollama;
    }

    public static GenerateResponse ToOllamaGenerateDelta(CompletionResponse chunk, string requestedModel)
        => new()
        {
            Model = chunk.Model ?? requestedModel,
            CreatedAt = DateTimeOffset.UtcNow,
            Response = chunk.Choices?.FirstOrDefault()?.Text ?? string.Empty,
            Done = false
        };

    public static GenerateResponse ToOllamaGenerateDone(string model, string? finishReason, OpenAiUsage? usage)
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

    public static EmbedResponse ToOllamaEmbed(OpenAiEmbeddingsResponse response, string requestedModel)
    {
        var vectors = (response.Data ?? [])
            .OrderBy(item => item.Index)
            .Select(item => ToVector(item.Embedding))
            .ToList();

        return new EmbedResponse
        {
            Model = response.Model ?? requestedModel,
            Embeddings = vectors,
            PromptEvalCount = response.Usage?.PromptTokens
        };
    }

    // ---- helpers ----------------------------------------------------------------------

    private static OpenAiChatMessage ToOpenAiMessage(ChatMessage message)
    {
        var openAi = new OpenAiChatMessage
        {
            Role = message.Role ?? "user",
            Content = ToOpenAiContent(message)
        };

        if (message.AdditionalProperties is not { } extras)
        {
            return openAi;
        }

        if (extras.TryGetValue("tool_call_id", out var toolCallId) && toolCallId.ValueKind == JsonValueKind.String)
        {
            openAi.ToolCallId = toolCallId.GetString();
        }

        if (extras.TryGetValue("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            openAi.ToolCalls = StringifyToolCallArguments(toolCalls);
        }

        return openAi;
    }

    /// <summary>
    /// A text-only message stays a plain string — that is what every upstream accepts and what
    /// the pre-vision wire looked like, so nothing changes for it. A message carrying Ollama's
    /// <c>images</c> array becomes OpenAI content parts on the way back out, so a vision request
    /// that arrived on <c>/v1</c> survives the double translation to a vLLM or hosted node.
    /// </summary>
    private static JsonElement ToOpenAiContent(ChatMessage message)
    {
        var text = message.Content ?? string.Empty;

        if (message.Images is not { ValueKind: JsonValueKind.Array } images || images.GetArrayLength() == 0)
        {
            return JsonSerializer.SerializeToElement(text);
        }

        var parts = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text }
        };

        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.String || image.GetString() is not { Length: > 0 } base64)
            {
                continue;
            }

            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{SniffMediaType(base64)};base64,{base64}" }
            });
        }

        return JsonSerializer.SerializeToElement(parts);
    }

    /// <summary>
    /// Ollama's <c>images</c> are bare base64 with no media type, but an OpenAI data URL needs
    /// one — so it is sniffed from the magic bytes rather than guessed at a default. An upstream
    /// handed <c>image/png</c> for a JPEG is a failure that looks like a bad model answer, which
    /// is exactly the class of quiet wrongness this codebase spends errors to avoid.
    /// </summary>
    private static string SniffMediaType(string base64)
    {
        // 16 base64 chars decode to 12 bytes — enough for every signature below, and a valid
        // standalone block, so no padding games.
        Span<byte> header = stackalloc byte[12];
        var prefix = base64.Length >= 16 ? base64[..16] : base64;

        if (!Convert.TryFromBase64String(prefix, header, out var written) || written < 4)
        {
            throw new OpenAiRequestException(
                "an image could not be forwarded upstream: its media type is unreadable",
                statusCode: 400);
        }

        var bytes = header[..written];

        if (bytes.StartsWith(PngSignature)) return "image/png";
        if (bytes.StartsWith(JpegSignature)) return "image/jpeg";
        if (bytes.StartsWith("GIF8"u8)) return "image/gif";
        if (written >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8)) return "image/webp";

        // Fail clean rather than silently mislabel: an upstream that can't carry this image
        // should say so here, not answer about something it never decoded.
        throw new OpenAiRequestException(
            "an image could not be forwarded upstream: only PNG, JPEG, GIF and WebP are recognised",
            statusCode: 400);
    }

    /// <summary>
    /// Ollama carries tool-call arguments as an object; OpenAI carries them as a JSON string,
    /// and a server handed the object will fail to parse it. This is the same asymmetry
    /// <see cref="ResponseTranslator.ExtractToolCalls"/> handles in the other direction.
    /// </summary>
    private static JsonElement StringifyToolCallArguments(JsonElement toolCalls)
    {
        var array = new JsonArray();

        foreach (var call in toolCalls.EnumerateArray())
        {
            if (JsonNode.Parse(call.GetRawText()) is not JsonObject node)
            {
                continue;
            }

            node["type"] ??= JsonValue.Create("function");

            if (node["function"] is JsonObject function
                && function["arguments"] is { } arguments
                && arguments is not JsonValue)
            {
                function["arguments"] = JsonValue.Create(arguments.ToJsonString());
            }

            array.Add(node);
        }

        return JsonSerializer.SerializeToElement(array);
    }

    private static Dictionary<string, JsonElement>? ToOllamaToolCalls(IReadOnlyList<OpenAiToolCall>? toolCalls)
    {
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return null;
        }

        var array = new JsonArray();

        foreach (var call in toolCalls)
        {
            JsonNode? arguments;
            try
            {
                arguments = JsonNode.Parse(call.Function.Arguments);
            }
            catch (JsonException)
            {
                // A model that emits malformed arguments is not a reason to drop the call —
                // pass the raw string through and let the caller decide.
                arguments = JsonValue.Create(call.Function.Arguments);
            }

            array.Add(new JsonObject
            {
                ["id"] = JsonValue.Create(call.Id),
                ["type"] = JsonValue.Create("function"),
                ["function"] = new JsonObject
                {
                    ["name"] = JsonValue.Create(call.Function.Name),
                    ["arguments"] = arguments
                }
            });
        }

        return new Dictionary<string, JsonElement>
        {
            ["tool_calls"] = JsonSerializer.SerializeToElement(array)
        };
    }

    private static void ApplyOptions(JsonElement? options, ChatCompletionRequest request)
    {
        if (options is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        request.Temperature = ReadDouble(element, "temperature");
        request.TopP = ReadDouble(element, "top_p");
        request.PresencePenalty = ReadDouble(element, "presence_penalty");
        request.FrequencyPenalty = ReadDouble(element, "frequency_penalty");
        request.Seed = ReadLong(element, "seed");
        request.MaxTokens = ReadInt(element, "num_predict");
        request.Stop = ReadElement(element, "stop");
    }

    private static void ApplyOptions(JsonElement? options, CompletionRequest request)
    {
        if (options is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        request.Temperature = ReadDouble(element, "temperature");
        request.TopP = ReadDouble(element, "top_p");
        request.PresencePenalty = ReadDouble(element, "presence_penalty");
        request.FrequencyPenalty = ReadDouble(element, "frequency_penalty");
        request.Seed = ReadLong(element, "seed");
        request.MaxTokens = ReadInt(element, "num_predict");
        request.Stop = ReadElement(element, "stop");
    }

    /// <summary>Ollama's <c>format</c> is <c>"json"</c> or a bare JSON schema object.</summary>
    private static ResponseFormat? ToResponseFormat(JsonElement format) => format.ValueKind switch
    {
        JsonValueKind.String when format.GetString() == "json" => new ResponseFormat { Type = "json_object" },
        JsonValueKind.Object => new ResponseFormat
        {
            Type = "json_schema",
            JsonSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["name"] = JsonValue.Create("response"),
                ["schema"] = JsonNode.Parse(format.GetRawText())
            })
        },
        _ => null
    };

    private static void ApplyDone(Dictionary<string, JsonElement> extras, string? finishReason)
    {
        // Ollama only knows "stop" and "length"; "tool_calls" is an OpenAI-ism and the calls
        // themselves already say so.
        var reason = finishReason == ResponseTranslator.LengthReason
            ? ResponseTranslator.LengthReason
            : ResponseTranslator.StopReason;

        extras["done_reason"] = JsonSerializer.SerializeToElement(reason);
    }

    private static void ApplyUsage(ChatResponse ollama, OpenAiUsage? usage)
    {
        if (usage is null)
        {
            return;
        }

        ollama.PromptEvalCount = usage.PromptTokens;
        ollama.EvalCount = usage.CompletionTokens;
    }

    private static void ApplyUsage(GenerateResponse ollama, OpenAiUsage? usage)
    {
        if (usage is null)
        {
            return;
        }

        ollama.PromptEvalCount = usage.PromptTokens;
        ollama.EvalCount = usage.CompletionTokens;
    }

    private static List<float> ToVector(object embedding)
    {
        if (embedding is not JsonElement element || element.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiRequestException(
                "upstream returned an embedding that was not a float array",
                statusCode: 502,
                type: OpenAiErrorTypes.ApiError);
        }

        var vector = new List<float>(element.GetArrayLength());

        foreach (var value in element.EnumerateArray())
        {
            vector.Add(value.GetSingle());
        }

        return vector;
    }

    private static double? ReadDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static long? ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static JsonElement? ReadElement(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : null;
}
