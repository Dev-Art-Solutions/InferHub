using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Shared.OpenAi;

/// <summary>
/// OpenAI request → Ollama request body. The output is the exact JSON the node already
/// knows how to run, so everything behind the coordinator's front door (job kinds, the
/// SignalR protocol, the retrieval pipeline) is untouched by the second dialect.
///
/// This is the client-facing half of the translation. <see cref="UpstreamTranslator"/> is the
/// mirror the node runs against an OpenAI-compatible server.
/// </summary>
public static class RequestTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToOllamaChat(ChatCompletionRequest request)
    {
        RejectMultipleChoices(request.N);

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new OpenAiRequestException("model is required", param: "model");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new OpenAiRequestException("messages is required and must not be empty", param: "messages");
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = TranslateMessages(request.Messages),
            ["stream"] = request.Stream ?? false
        };

        var options = BuildOptions(
            request.Temperature,
            request.TopP,
            request.PresencePenalty,
            request.FrequencyPenalty,
            request.Seed,
            request.MaxCompletionTokens ?? request.MaxTokens,
            request.Stop);

        if (options.Count > 0)
        {
            body["options"] = options;
        }

        if (TranslateResponseFormat(request.ResponseFormat) is { } format)
        {
            body["format"] = format;
        }

        // Ollama speaks the same tool schema, so these ride through untranslated.
        if (request.Tools is { } tools && tools.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            body["tools"] = ToNode(tools);
        }

        if (request.ToolChoice is { } toolChoice && toolChoice.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            body["tool_choice"] = ToNode(toolChoice);
        }

        return body.ToJsonString(JsonOptions);
    }

    public static string ToOllamaGenerate(CompletionRequest request)
    {
        RejectMultipleChoices(request.N);

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new OpenAiRequestException("model is required", param: "model");
        }

        var prompt = ExtractPrompt(request.Prompt);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["prompt"] = prompt,
            ["stream"] = request.Stream ?? false
        };

        var options = BuildOptions(
            request.Temperature,
            request.TopP,
            request.PresencePenalty,
            request.FrequencyPenalty,
            request.Seed,
            request.MaxTokens,
            request.Stop);

        if (options.Count > 0)
        {
            body["options"] = options;
        }

        return body.ToJsonString(JsonOptions);
    }

    public static string ToOllamaEmbed(OpenAiEmbeddingsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new OpenAiRequestException("model is required", param: "model");
        }

        var input = request.Input;
        if (input is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new OpenAiRequestException("input is required", param: "input");
        }

        // Ollama's /api/embed takes string | string[] under "input" — the same shape, so the
        // element rides through once validated.
        ValidateEmbeddingInput(value);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = ToNode(value)
        };

        return body.ToJsonString(JsonOptions);
    }

    private static void ValidateEmbeddingInput(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            return;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiRequestException("input must be a string or an array of strings", param: "input");
        }

        if (input.GetArrayLength() == 0)
        {
            throw new OpenAiRequestException("input must not be empty", param: "input");
        }

        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                // Token-array inputs would need a tokenizer at the edge; we do not have one,
                // and guessing would return vectors for the wrong text.
                throw new OpenAiRequestException(
                    "input must be a string or an array of strings; token arrays are not supported",
                    param: "input");
            }
        }
    }

    private static JsonArray TranslateMessages(IReadOnlyList<OpenAiChatMessage> messages)
    {
        var array = new JsonArray();

        foreach (var message in messages)
        {
            var content = ExtractContent(message.Content);

            var item = new JsonObject
            {
                ["role"] = message.Role ?? "user",
                ["content"] = content.Text
            };

            if (content.Images.Count > 0)
            {
                var images = new JsonArray();
                foreach (var image in content.Images)
                {
                    images.Add(JsonValue.Create(image));
                }
                item["images"] = images;
            }

            if (!string.IsNullOrEmpty(message.Name))
            {
                item["name"] = message.Name;
            }

            if (message.ToolCalls is { } toolCalls && toolCalls.ValueKind == JsonValueKind.Array)
            {
                item["tool_calls"] = TranslateToolCalls(toolCalls);
            }

            if (!string.IsNullOrEmpty(message.ToolCallId))
            {
                item["tool_call_id"] = message.ToolCallId;
            }

            array.Add(item);
        }

        return array;
    }

    /// <summary>
    /// The text and the images of one message. Ollama carries them as two fields —
    /// <c>content</c> and <c>images</c> — where OpenAI carries them as one array of parts, so
    /// the translation has to split rather than join.
    /// </summary>
    internal readonly record struct MessageContent(string Text, IReadOnlyList<string> Images)
    {
        public static readonly MessageContent Empty = new(string.Empty, []);
    }

    /// <summary>
    /// <c>content</c> is a string, or an array of parts. Text parts are joined; <c>image_url</c>
    /// parts become Ollama's base64 <c>images</c> array. Audio and video parts are still rejected
    /// outright rather than dropped, so a client gets an error instead of a confidently wrong
    /// answer about something we never sent.
    /// </summary>
    internal static MessageContent ExtractContent(JsonElement? content)
    {
        if (content is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return MessageContent.Empty;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return new MessageContent(value.GetString() ?? string.Empty, []);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiRequestException("message content must be a string or an array of content parts", param: "messages");
        }

        var parts = new List<string>();
        var images = new List<string>();

        foreach (var part in value.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                parts.Add(part.GetString() ?? string.Empty);
                continue;
            }

            var type = part.ValueKind == JsonValueKind.Object && part.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            if (type == "image_url")
            {
                images.Add(ExtractImage(part));
                continue;
            }

            if (type != "text")
            {
                throw new OpenAiRequestException(
                    $"content part of type '{type ?? "unknown"}' is not supported; text and image_url parts are",
                    param: "messages");
            }

            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }

        return new MessageContent(string.Join("\n", parts), images);
    }

    /// <summary>
    /// One <c>image_url</c> part → the bare base64 Ollama wants. Only <c>data:</c> URLs are
    /// accepted: fetching an <c>http(s)</c> image would make the coordinator issue outbound
    /// requests to caller-supplied URLs (an SSRF surface), and would pull third-party bytes
    /// through a hop that is supposed to retain nothing (rule 7). Inlining a hosted image is
    /// the caller's job, and every OpenAI SDK can already do it.
    /// </summary>
    private static string ExtractImage(JsonElement part)
    {
        var url = part.TryGetProperty("image_url", out var wrapper) && wrapper.ValueKind == JsonValueKind.Object
            && wrapper.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString()
                : null;

        if (string.IsNullOrEmpty(url))
        {
            throw new OpenAiRequestException("image_url.url is required on an image_url content part", param: "messages");
        }

        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenAiRequestException(
                "image_url.url must be a data: URL with base64 image data; the coordinator does not fetch remote images",
                param: "messages");
        }

        var marker = url.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            throw new OpenAiRequestException(
                "image_url.url must be a base64 data: URL (data:image/...;base64,...)",
                param: "messages");
        }

        var payload = url[(marker + ";base64,".Length)..];

        // Validate rather than forward blindly: a node rejecting malformed base64 seconds later,
        // from behind a GPU queue, is a much worse error than a 400 here.
        if (payload.Length == 0 || !Convert.TryFromBase64String(payload, new byte[payload.Length], out _))
        {
            throw new OpenAiRequestException("image_url.url does not carry valid base64 data", param: "messages");
        }

        return payload;
    }

    private static string ExtractPrompt(JsonElement? prompt)
    {
        if (prompt is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new OpenAiRequestException("prompt is required", param: "prompt");
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        // A single-element array of strings is the shape some SDKs emit for one prompt.
        if (value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() == 1
            && value[0].ValueKind == JsonValueKind.String)
        {
            return value[0].GetString() ?? string.Empty;
        }

        throw new OpenAiRequestException(
            "prompt must be a string; batched and token-array prompts are not supported",
            param: "prompt");
    }

    private static JsonObject BuildOptions(
        double? temperature,
        double? topP,
        double? presencePenalty,
        double? frequencyPenalty,
        long? seed,
        int? maxTokens,
        JsonElement? stop)
    {
        var options = new JsonObject();

        if (temperature is { } t) options["temperature"] = t;
        if (topP is { } p) options["top_p"] = p;
        if (presencePenalty is { } pp) options["presence_penalty"] = pp;
        if (frequencyPenalty is { } fp) options["frequency_penalty"] = fp;
        if (seed is { } s) options["seed"] = s;
        if (maxTokens is { } max) options["num_predict"] = max;

        if (TranslateStop(stop) is { } stopArray)
        {
            options["stop"] = stopArray;
        }

        return options;
    }

    // OpenAI accepts a bare string or an array; Ollama always wants an array.
    internal static JsonArray? TranslateStop(JsonElement? stop)
    {
        if (stop is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return new JsonArray(JsonValue.Create(value.GetString()));
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiRequestException("stop must be a string or an array of strings", param: "stop");
        }

        var array = new JsonArray();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new OpenAiRequestException("stop must be a string or an array of strings", param: "stop");
            }
            array.Add(JsonValue.Create(item.GetString()));
        }

        return array.Count == 0 ? null : array;
    }

    internal static JsonNode? TranslateResponseFormat(ResponseFormat? format)
    {
        if (format is null || string.IsNullOrEmpty(format.Type) || format.Type == "text")
        {
            return null;
        }

        if (format.Type == "json_object")
        {
            return JsonValue.Create("json");
        }

        if (format.Type == "json_schema")
        {
            // Ollama's "format" takes the bare JSON schema object, which sits one level down
            // inside OpenAI's { json_schema: { name, schema } } wrapper.
            if (format.JsonSchema is { } wrapper && wrapper.ValueKind == JsonValueKind.Object)
            {
                if (wrapper.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Object)
                {
                    return ToNode(schema);
                }
                return ToNode(wrapper);
            }

            throw new OpenAiRequestException("response_format.json_schema is required when type is 'json_schema'", param: "response_format");
        }

        throw new OpenAiRequestException($"response_format type '{format.Type}' is not supported", param: "response_format");
    }

    private static void RejectMultipleChoices(int? n)
    {
        // Serving one choice and calling it n would be a quiet lie; the router dispatches a
        // single job and there is no second sample to return.
        if (n is > 1)
        {
            throw new OpenAiRequestException("n > 1 is not supported", param: "n");
        }
    }

    /// <summary>
    /// A prior assistant turn's <c>tool_calls</c>, back into the Ollama shape. The one
    /// non-passthrough part is <c>function.arguments</c>: OpenAI clients serialize it as a JSON
    /// *string* (and our own streamed <c>delta.tool_calls</c> does too), but Ollama emits and
    /// expects it as an *object*. Without parsing it back, the round-trip the streaming feature
    /// exists for — streamed call → tool result → grounded answer — reaches the model as a
    /// string it cannot read, and the model answers empty. Verified live against a real node.
    /// </summary>
    private static JsonArray TranslateToolCalls(JsonElement toolCalls)
    {
        var array = new JsonArray();

        foreach (var call in toolCalls.EnumerateArray())
        {
            if (ToNode(call) is not { } node)
            {
                continue;
            }

            if (node is JsonObject obj
                && obj["function"] is JsonObject function
                && function["arguments"] is JsonValue argument
                && argument.TryGetValue<string>(out var argumentString))
            {
                function["arguments"] = ParseArgumentsOrKeep(argumentString);
            }

            array.Add(node);
        }

        return array;
    }

    private static JsonNode? ParseArgumentsOrKeep(string arguments)
    {
        // Leave it as-is if it isn't valid JSON — a best-effort object beats throwing on a
        // client that sent something odd.
        try
        {
            return JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static JsonNode? ToNode(JsonElement element)
        => JsonNode.Parse(element.GetRawText());
}

/// <summary>
/// A client-side error, surfaced through the OpenAI error envelope. The status is a plain
/// <c>int</c> rather than an ASP.NET <c>StatusCodes</c> constant — this type now lives in
/// InferHub.Shared, which stays host-agnostic (rule 2).
/// </summary>
public sealed class OpenAiRequestException(
    string message,
    int statusCode = 400,
    string type = OpenAiErrorTypes.InvalidRequest,
    string? param = null,
    string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Type { get; } = type;

    public string? Param { get; } = param;

    public string? Code { get; } = code;
}
