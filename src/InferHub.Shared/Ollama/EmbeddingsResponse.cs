using System.Text.Json.Serialization;

namespace InferHub.Shared.Ollama;

public sealed class EmbeddingsResponse
{
    [JsonPropertyName("embedding")]
    public List<float> Embedding { get; set; } = new();
}
