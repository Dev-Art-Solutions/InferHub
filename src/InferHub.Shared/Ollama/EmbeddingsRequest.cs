using System.Text.Json.Serialization;

namespace InferHub.Shared.Ollama;

// Legacy /api/embeddings (single string, single embedding response). Kept for clients
// that have not migrated to the batch-capable /api/embed.
public sealed class EmbeddingsRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeepAlive { get; set; }
}
