using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.Ollama;

public sealed record OllamaTagsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<ModelInfo> Models);
