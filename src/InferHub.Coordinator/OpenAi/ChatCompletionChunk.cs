using System.Text.Json.Serialization;

namespace InferHub.Coordinator.OpenAi;

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
/// rather than serialize as null — clients concatenate <c>delta.content</c> blindly.
/// </summary>
public sealed record ChatCompletionDelta(
    [property: JsonPropertyName("role")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Role,
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Content);
