using System.Text.Json.Serialization;

namespace InferHub.Shared.OpenAi;

public sealed record ChatCompletionChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatCompletionChunkChoice> Choices,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiUsage? Usage)
{
    [JsonPropertyName("object")]
    public string Object => "chat.completion.chunk";
}

public sealed record ChatCompletionChunkChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] ChatCompletionDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason)
{
    [JsonPropertyName("logprobs")]
    public object? Logprobs => null;
}

/// <summary>
/// Only the opening chunk carries <c>role</c>; every later chunk carries <c>content</c>
/// alone, and the terminal chunk carries an empty delta. Omitted fields must stay omitted
/// rather than serialize as null — clients concatenate <c>delta.content</c> blindly, so a
/// stray <c>tool_calls: null</c> on ordinary text deltas would be a wire change.
/// </summary>
public sealed record ChatCompletionDelta(
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role,
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Content,
    [property: JsonPropertyName("tool_calls")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<StreamingToolCall>? ToolCalls = null);

/// <summary>
/// A tool call as it appears inside a streaming <c>delta.tool_calls</c> frame. Identical to
/// <see cref="OpenAiToolCall"/> plus the required <c>index</c> the streaming spec uses to
/// reassemble calls across frames. Ollama emits <c>message.tool_calls</c> only in its final
/// (<c>done:true</c>) chunk, so the whole call lands in a single delta — we do not fabricate
/// OpenAI-style argument-fragment streaming that Ollama never sent (rule 6 / phase 27 note).
/// </summary>
public sealed record StreamingToolCall(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] OpenAiToolCallFunction Function)
{
    [JsonPropertyName("type")]
    public string Type => "function";
}
