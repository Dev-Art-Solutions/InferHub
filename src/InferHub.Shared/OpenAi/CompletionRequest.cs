using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.OpenAi;

/// <summary>Legacy <c>/v1/completions</c>. Maps to the Ollama <c>generate</c> job kind.</summary>
public sealed class CompletionRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    // OpenAI allows string | string[] | token arrays. Only the single-string form is
    // supported; anything else is rejected rather than silently truncated.
    [JsonPropertyName("prompt")]
    public JsonElement? Prompt { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("stream_options")]
    public StreamOptions? StreamOptions { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("presence_penalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
