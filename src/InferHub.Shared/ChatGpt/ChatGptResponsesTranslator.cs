using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Ollama;
using InferHub.Shared.Upstream;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// Ollama JSON → a Responses API body that ChatGPT plan usage accepts, and a stream's end → the
/// Ollama terminal chunk (phase 103).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is dropped, and why it is dropped rather than refused (103 D4).</b> Plan usage rejects
/// <c>temperature</c>, <c>top_p</c>, <c>max_output_tokens</c> and the rest of Ollama's
/// <c>options</c>; every Ollama client sends some of them, so refusing a request that carried one
/// would refuse nearly every request. They are sampling hints, and the answer still answers the
/// question. <b>Tools are the opposite case</b>: a request that declares tools is relying on them,
/// and silently answering it without them is a 200 that looks right — so that is a 400 naming the
/// reason.
/// </para>
/// <para>
/// A system turn becomes <c>instructions</c>: the vendor rejects an explicit system message item.
/// History is sent whole every time (<c>previous_response_id</c> is not available over HTTP), which
/// is what an Ollama client does anyway — and is rule 7 holding without effort.
/// </para>
/// </remarks>
public static class ChatGptResponsesTranslator
{
    private const string SystemRole = "system";

    public static JsonObject FromChat(ChatRequest request)
    {
        RefuseTools(request.AdditionalProperties);

        var instructions = new List<string>();
        var input = new JsonArray();

        foreach (var message in request.Messages ?? [])
        {
            var role = message.Role?.Trim().ToLowerInvariant();

            switch (role)
            {
                case SystemRole:
                case "developer":
                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        instructions.Add(message.Content.Trim());
                    }

                    break;

                case "user":
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = UserContent(message.Content, message.Images) });
                    break;

                case "assistant":
                    input.Add(new JsonObject { ["role"] = "assistant", ["content"] = message.Content ?? string.Empty });
                    break;

                case "tool":
                    throw ToolsRefused();

                default:
                    throw new ChatGptUpstreamException(400, null,
                        $"ChatGPT: a message has role '{message.Role}'; the roles a ChatGPT-plan request carries are system, user and assistant.");
            }
        }

        return Body(request.Model, instructions, input, request.AdditionalProperties);
    }

    public static JsonObject FromGenerate(GenerateRequest request)
    {
        RefuseTools(request.AdditionalProperties);

        var instructions = new List<string>();

        if (Str(request.AdditionalProperties, "system") is { } system)
        {
            instructions.Add(system);
        }

        JsonElement? images = request.AdditionalProperties is not null
            && request.AdditionalProperties.TryGetValue("images", out var value)
                ? value
                : null;

        var input = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = UserContent(request.Prompt, images) }
        };

        return Body(request.Model, instructions, input, request.AdditionalProperties);
    }

    internal static ChatResponse ChatDone(string model, ChatGptResponsesClient.StreamState state)
    {
        var done = new ChatResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Done = true,
            PromptEvalCount = state.InputTokens,
            EvalCount = state.OutputTokens
        };

        (done.AdditionalProperties ??= [])["done_reason"] = JsonSerializer.SerializeToElement(state.Length ? "length" : "stop");
        return done;
    }

    internal static GenerateResponse GenerateDone(string model, ChatGptResponsesClient.StreamState state)
    {
        var done = new GenerateResponse
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow,
            Done = true,
            PromptEvalCount = state.InputTokens,
            EvalCount = state.OutputTokens
        };

        (done.AdditionalProperties ??= [])["done_reason"] = JsonSerializer.SerializeToElement(state.Length ? "length" : "stop");
        return done;
    }

    private static JsonObject Body(string? model, List<string> instructions, JsonArray input, Dictionary<string, JsonElement>? extras)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ChatGptUpstreamException(400, null, "ChatGPT: model is required.");
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = input,
            ["store"] = false,
            ["stream"] = true
        };

        if (instructions.Count > 0)
        {
            body["instructions"] = string.Join("\n\n", instructions);
        }

        if (Format(extras) is { } format)
        {
            body["text"] = new JsonObject { ["format"] = format };
        }

        return body;
    }

    /// <summary>
    /// Ollama's <c>format</c>: <c>"json"</c> is JSON mode, an object is a schema. Both are the
    /// Responses API's own <c>text.format</c>, which is not on the plan-usage refusal list.
    /// </summary>
    private static JsonObject? Format(Dictionary<string, JsonElement>? extras)
    {
        if (extras is null || !extras.TryGetValue("format", out var format))
        {
            return null;
        }

        return format.ValueKind switch
        {
            JsonValueKind.String when format.GetString() == "json" => new JsonObject { ["type"] = "json_object" },
            JsonValueKind.Object => new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = "output",
                ["schema"] = JsonNode.Parse(format.GetRawText())
            },
            _ => null
        };
    }

    /// <summary>A plain string, or text plus <c>input_image</c> parts as data URLs.</summary>
    private static JsonNode UserContent(string? text, JsonElement? images)
    {
        if (images is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0)
        {
            return JsonValue.Create(text ?? string.Empty);
        }

        var parts = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = text ?? string.Empty } };

        foreach (var image in list.EnumerateArray())
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
                throw new ChatGptUpstreamException(400, null, $"ChatGPT: {ex.Message}");
            }

            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{mediaType};base64,{base64}" });
        }

        return parts;
    }

    private static void RefuseTools(Dictionary<string, JsonElement>? extras)
    {
        if (extras is not null
            && extras.TryGetValue("tools", out var tools)
            && tools.ValueKind == JsonValueKind.Array
            && tools.GetArrayLength() > 0)
        {
            throw ToolsRefused();
        }
    }

    private static ChatGptUpstreamException ToolsRefused()
        => new(400, "subscription_sharing_unsupported_capability",
            "ChatGPT: tool calling is not supported by the chatgpt provider in this release; the request was not sent. "
            + "Map the model to another provider or a node for tool use.");

    private static string? Str(Dictionary<string, JsonElement>? extras, string name)
        => extras is not null
           && extras.TryGetValue(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;
}
