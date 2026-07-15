using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Reorders a candidate set by relevance to the query. The seam exists so a dedicated cross-encoder
/// (Cohere, Jina, a TEI server) can slot in later without touching the pipeline; v2.6 ships exactly
/// one implementation, <see cref="LlmReranker"/>, which reuses a model already on the fleet.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// Return <paramref name="candidates"/> reordered best-first. A reranker that cannot reach the
    /// fleet, times out, or cannot parse a score <b>must return the candidates in their original
    /// order</b> rather than throw — reranking is an improvement, never a dependency.
    /// </summary>
    Task<IReadOnlyList<VectorMatch>> RerankAsync(
        string query,
        IReadOnlyList<VectorMatch> candidates,
        string? model,
        CancellationToken cancellationToken);
}
