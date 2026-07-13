using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Coordinator.OpenAi;

public sealed class OpenAiEmbeddingsRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    // string | string[]. Token-array inputs are not supported — we have no tokenizer at
    // the edge and guessing one would produce silently wrong vectors.
    [JsonPropertyName("input")]
    public JsonElement? Input { get; set; }

    /// <summary>
    /// <c>float</c> or <c>base64</c>. The OpenAI Python SDK asks for <c>base64</c> by
    /// default, so this is load-bearing rather than a nicety.
    /// </summary>
    [JsonPropertyName("encoding_format")]
    public string? EncodingFormat { get; set; }

    [JsonPropertyName("dimensions")]
    public int? Dimensions { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
