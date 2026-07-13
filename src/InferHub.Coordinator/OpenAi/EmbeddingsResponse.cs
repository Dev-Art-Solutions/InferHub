using System.Text.Json.Serialization;

namespace InferHub.Coordinator.OpenAi;

public sealed record OpenAiEmbeddingsResponse(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiEmbedding> Data,
    [property: JsonPropertyName("usage")] OpenAiEmbeddingsUsage Usage)
{
    [JsonPropertyName("object")]
    public string Object => "list";
}

/// <summary>
/// <see cref="Embedding"/> is <c>float[]</c> under <c>encoding_format: float</c> and a
/// base64 <c>string</c> under <c>base64</c> — the wire field is the same either way, so it
/// is typed as <c>object</c> and populated by the translator.
/// </summary>
public sealed record OpenAiEmbedding(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] object Embedding)
{
    [JsonPropertyName("object")]
    public string Object => "embedding";
}

public sealed record OpenAiEmbeddingsUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
