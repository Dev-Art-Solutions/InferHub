using System.Text.Json.Serialization;

namespace InferHub.Shared.Ollama;

public sealed class EmbedResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("embeddings")]
    public List<List<float>> Embeddings { get; set; } = new();

    [JsonPropertyName("total_duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalDuration { get; set; }

    [JsonPropertyName("load_duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? LoadDuration { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PromptEvalCount { get; set; }
}
