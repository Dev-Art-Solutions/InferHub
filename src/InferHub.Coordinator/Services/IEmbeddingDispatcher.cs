using System.Text.Json;
using InferHub.Shared.Ollama;

namespace InferHub.Coordinator.Services;

public interface IEmbeddingDispatcher
{
    // Wire-shaped embed for callers that already speak Ollama's /api/embed body.
    Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken);

    // Convenience for the vector store: embed a single text and return the raw vector.
    Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken);
}
