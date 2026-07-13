using System.Text.Json.Serialization;

namespace InferHub.Shared.OpenAi;

public sealed record ChatCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatCompletionChoice> Choices,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiUsage? Usage)
{
    [JsonPropertyName("object")]
    public string Object => "chat.completion";
}

public sealed record ChatCompletionChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] ChatCompletionResponseMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason)
{
    // OpenAI marks logprobs as required-but-nullable; SDKs read it unconditionally.
    [JsonPropertyName("logprobs")]
    public object? Logprobs => null;
}

public sealed record ChatCompletionResponseMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<OpenAiToolCall>? ToolCalls);

public sealed record OpenAiToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] OpenAiToolCallFunction Function)
{
    [JsonPropertyName("type")]
    public string Type => "function";
}

/// <summary>
/// Note <see cref="Arguments"/> is a JSON <em>string</em>, not an object: Ollama emits the
/// arguments as a nested object, OpenAI clients expect a serialized string and will throw
/// while parsing if handed the object.
/// </summary>
public sealed record OpenAiToolCallFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

public sealed record OpenAiUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
