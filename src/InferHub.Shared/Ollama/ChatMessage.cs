using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Ollama;

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("images")]
    public JsonElement? Images { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
