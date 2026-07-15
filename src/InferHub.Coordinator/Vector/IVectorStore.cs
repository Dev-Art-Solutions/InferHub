using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

public interface IVectorStore
{
    Task<CollectionInfo> CreateCollectionAsync(string name, int dimension, string? distance, CancellationToken cancellationToken = default);

    Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default);

    Task<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken = default);

    Task<VectorRecord> UpsertAsync(string collection, VectorUpsert upsert, CancellationToken cancellationToken = default);

    Task<VectorRecord?> GetAsync(string collection, string id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorMatch>> QueryAsync(string collection, VectorQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keyword (lexical) search: the top <paramref name="k"/> records whose chunk text best matches
    /// <paramref name="query"/> by the provider's full-text ranking (BM25 under <c>local</c>,
    /// <c>ts_rank_cd</c> under <c>postgres</c>). This is the branch pure vector search is bad at —
    /// literal identifiers, error codes, surnames — and the keyword half of hybrid retrieval. Scores
    /// are on the provider's own scale and are <b>not</b> comparable to <see cref="QueryAsync"/>'s
    /// distances, which is exactly why fusion is by rank (RRF) and not by blending the two numbers.
    /// </summary>
    Task<IReadOnlyList<VectorMatch>> SearchKeywordAsync(string collection, string query, int k, CancellationToken cancellationToken = default);

    /// <summary>
    /// Metadata-ordered scan: records whose metadata matches every key in <paramref name="filter"/>
    /// (all records when it is null or empty), ordered by id, starting after <paramref name="afterId"/>.
    /// Embeddings are not fetched — see <see cref="VectorEntry"/>.
    /// <para>
    /// This is what lets phase 23 keep its promise that ingestion writes to the vector store and
    /// nowhere else: a document is a set of chunks sharing a <c>documentId</c> in their metadata,
    /// and this is how that set is found. No documents table, no second lifecycle.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<VectorEntry>> ScanAsync(
        string collection,
        IReadOnlyDictionary<string, string>? filter,
        int limit,
        string? afterId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete every record matching <paramref name="filter"/>; returns how many went. The filter
    /// must be non-empty — an empty one would mean "delete the collection's contents", which is
    /// what <see cref="DropCollectionAsync"/> is for, and is not something a caller should be able
    /// to ask for by accident.
    /// </summary>
    Task<int> DeleteByFilterAsync(
        string collection,
        IReadOnlyDictionary<string, string> filter,
        CancellationToken cancellationToken = default);
}
