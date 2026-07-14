using System.Text.Json.Serialization;

namespace InferHub.Coordinator.Ingestion;

/// <summary>
/// One unit of extracted text. <see cref="Page"/> is 1-based and set only by extractors that
/// have a real notion of pages (PDF today); everything else carries <c>null</c> and a citation
/// simply omits the page.
/// </summary>
public sealed record ExtractedPage(int? Page, string Text);

public sealed record ExtractedDocument(string MediaType, IReadOnlyList<ExtractedPage> Pages)
{
    public int TotalChars => Pages.Sum(p => p.Text.Length);
}

/// <summary>A chunk of a document, before it has a vector.</summary>
public sealed record DocumentChunk(int Index, string Text, int? Page);

/// <summary>
/// A document as it appears to a caller. Derived entirely from chunk metadata in the vector
/// store — there is no documents table (D1).
/// </summary>
public sealed record DocumentSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("ingestedAt")] DateTimeOffset? IngestedAt,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("mediaType")] string? MediaType,
    [property: JsonPropertyName("status")] string Status);

/// <summary>
/// The outcome of an ingest call. <c>status</c> is <c>ingested</c>, <c>unchanged</c> (identical
/// bytes already present, no work done) or <c>partial</c> (some batches failed after retries —
/// <see cref="Error"/> says how, and the document is left in the store with the chunks that did
/// embed). A half-ingested document that claims success is worse than a failure.
/// </summary>
public sealed record IngestResult(
    [property: JsonPropertyName("documentId")] string DocumentId,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("chunksEmbedded")] int ChunksEmbedded,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("contentHash")] string ContentHash,
    [property: JsonPropertyName("error")] string? Error = null)
{
    public const string Ingested = "ingested";
    public const string Unchanged = "unchanged";
    public const string Partial = "partial";
}

/// <summary>
/// Metadata keys written onto every chunk. The document model *is* these keys — there is no
/// documents table (D1).
/// <para>
/// Note there is no <c>status</c> key. A document is <c>partial</c> when the chunks actually in
/// the store are fewer than the <see cref="ChunkCount"/> its chunks claim, and that comparison is
/// made at read time. Writing a status onto the chunks instead would mean rewriting all of them
/// whenever the verdict changed, and would let the stored status drift away from the stored
/// chunks — the one thing a partial-ingest marker exists to prevent.
/// </para>
/// </summary>
public static class ChunkMetadata
{
    public const string DocumentId = "documentId";
    public const string ChunkIndex = "chunkIndex";
    public const string ChunkCount = "chunkCount";
    public const string ContentHash = "contentHash";
    public const string IngestedAt = "ingestedAt";
    public const string Source = "source";
    public const string MediaType = "mediaType";
    public const string Bytes = "bytes";
    public const string Page = "page";
}

/// <summary>The upload was a format we do not read. Surfaces as a 415.</summary>
public sealed class UnsupportedMediaTypeException(string message) : InvalidOperationException(message);

/// <summary>
/// The bytes were a format we read, but they yielded no usable text — a scanned PDF, an empty
/// file. Surfaces as a 422. It never degrades into an empty document: a corpus that silently
/// retrieves nothing is the single most common way a RAG system lies to its owner (D4).
/// </summary>
public sealed class ExtractionFailedException(string message) : InvalidOperationException(message);
