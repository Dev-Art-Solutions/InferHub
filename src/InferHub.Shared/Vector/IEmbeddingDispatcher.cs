using System.Text.Json;
using InferHub.Shared.Ollama;

namespace InferHub.Shared.Vector;

public interface IEmbeddingDispatcher
{
    // Wire-shaped embed for callers that already speak Ollama's /api/embed body.
    Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken);

    // Convenience for the vector store: embed a single text and return the raw vector.
    Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken);
}

/// <summary>
/// Nothing can embed: on the hub, no node advertises the model; on a solo node (phase 38), this
/// box's own backend does not serve it. Distinguished from an ordinary failure because it is not
/// going to fix itself in 400 ms — <c>IngestionPipeline</c> deliberately does not retry it, and
/// <c>RetrievalPipeline</c> hands it to <c>Retrieval:OnMissing</c>.
/// </summary>
public sealed class NoEmbeddingNodeException(string model)
    : InvalidOperationException($"no node is advertising embedding model '{model}'")
{
    public string Model { get; } = model;
}
