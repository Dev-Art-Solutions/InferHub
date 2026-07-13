using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// OpenAI request → Ollama request body. The output is the exact JSON the node already
/// knows how to run, so everything behind the coordinator's front door (job kinds, the
/// SignalR protocol, the retrieval pipeline) is untouched by the second dialect.
/// </summary>
internal static class RequestTranslator
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
            var item = new JsonObject
            {
                ["role"] = message.Role ?? "user",
                ["content"] = ExtractContent(message.Content)
            };

            if (!string.IsNullOrEmpty(message.Name))
            {
                item["name"] = message.Name;
            }

            if (message.ToolCalls is { } toolCalls && toolCalls.ValueKind == JsonValueKind.Array)
            {
                item["tool_calls"] = ToNode(toolCalls);
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
    /// <c>content</c> is a string, or an array of parts. Text parts are joined; an image or
    /// audio part is rejected outright rather than dropped, so a multimodal client gets an
    /// error instead of a confidently wrong answer about an image we never sent.
    /// </summary>
    internal static string ExtractContent(JsonElement? content)
    {
        if (content is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiRequestException("message content must be a string or an array of content parts", param: "messages");
        }

        var parts = new List<string>();

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

            if (type != "text")
            {
                throw new OpenAiRequestException(
                    $"content part of type '{type ?? "unknown"}' is not supported; only text parts are",
                    param: "messages");
            }

            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }

        return string.Join("\n", parts);
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

    private static JsonNode? ToNode(JsonElement element)
        => JsonNode.Parse(element.GetRawText());
}

/// <summary>A client-side error, surfaced through the OpenAI error envelope.</summary>
internal sealed class OpenAiRequestException(
    string message,
    int statusCode = StatusCodes.Status400BadRequest,
    string type = OpenAiErrorTypes.InvalidRequest,
    string? param = null,
    string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Type { get; } = type;

    public string? Param { get; } = param;

    public string? Code { get; } = code;
}
