using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Ollama;

public sealed class EmbedRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("input")]
    public JsonElement Input { get; set; }

    [JsonPropertyName("truncate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Truncate { get; set; }

    [JsonPropertyName("keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeepAlive { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
