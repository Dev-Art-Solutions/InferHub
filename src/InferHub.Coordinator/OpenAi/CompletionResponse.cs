using System.Text.Json.Serialization;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// Legacy completions use one shape for both streamed and blocking responses — unlike chat,
/// there is no separate chunk object.
/// </summary>
public sealed record CompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<CompletionChoice> Choices,
    [property: JsonPropertyName("usage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenAiUsage? Usage)
{
    [JsonPropertyName("object")]
    public string Object => "text_completion";
}

public sealed record CompletionChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("finish_reason")] string? FinishReason)
{
    [JsonPropertyName("logprobs")]
    public object? Logprobs => null;
}
