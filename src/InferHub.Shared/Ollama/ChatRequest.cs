using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Ollama;

public sealed class ChatRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<ChatMessage>? Messages { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("options")]
    public JsonElement? Options { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
