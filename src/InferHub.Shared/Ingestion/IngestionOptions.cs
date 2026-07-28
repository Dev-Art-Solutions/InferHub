
namespace InferHub.Shared.Ingestion;

/// <summary>
/// Document-ingestion settings. Inert unless <c>VectorStore:Enabled</c> — ingestion writes
/// to the vector store and nowhere else, so with no store there is nothing to ingest into.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Target chunk size, in characters. The chunker splits on the largest boundary that fits.</summary>
    public int MaxChars { get; set; } = 1200;

    /// <summary>Characters of tail context repeated at the head of the next chunk.</summary>
    public int OverlapChars { get; set; } = 150;

    /// <summary>Upload ceiling. A body larger than this is rejected before any work is done.</summary>
    public long MaxDocumentBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Chunks embedded per dispatched batch. Also the cap on chunks in flight (backpressure).</summary>
    public int EmbeddingBatchSize { get; set; } = 16;

    /// <summary>Embedding model. Empty = fall back to <c>VectorStore:DefaultEmbeddingModel</c>.</summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>Attempts per batch before the document is marked <c>partial</c>.</summary>
    public int MaxRetriesPerBatch { get; set; } = 3;
}

