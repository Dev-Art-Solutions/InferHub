using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// A vector store that can fuse a dense (embedding) and a sparse (lexical) query <em>server-side</em>
/// in a single round trip, rather than the hub running two branches and fusing them itself with
/// <see cref="HybridSearch"/>. Qdrant implements this for collections created hybrid-capable (v3.2+);
/// <see cref="RetrievalPipeline"/> prefers it over hub RRF when the store advertises it, and falls
/// back to hub fusion for a dense-only collection created on 3.1.
/// <para>
/// Deliberately a capability interface and not a method on <see cref="IVectorStore"/>: only one
/// provider can do this, and the seam that already carries three engines should not grow a method
/// the other two have to fake. This is the same "one implementation behind a seam" shape as
/// <see cref="IReranker"/>.
/// </para>
/// </summary>
internal interface IServerSideHybridSearch
{
    /// <summary>
    /// True when <paramref name="collection"/> was created hybrid-capable (it has a sparse vector).
    /// A dense-only collection created before the sparse vector existed returns false, and the caller
    /// fuses on the hub instead. Throws <see cref="KeyNotFoundException"/> if the collection is absent,
    /// exactly as the rest of the store does.
    /// </summary>
    Task<bool> SupportsServerSideHybridAsync(string collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// One fused query: the dense <paramref name="denseVector"/> and a sparse vector computed from
    /// <paramref name="queryText"/>, combined by reciprocal rank fusion inside the engine, returning
    /// the top <paramref name="k"/>.
    /// </summary>
    Task<IReadOnlyList<VectorMatch>> SearchHybridAsync(
        string collection,
        float[] denseVector,
        string queryText,
        int k,
        IReadOnlyDictionary<string, string>? filter,
        CancellationToken cancellationToken = default);
}
