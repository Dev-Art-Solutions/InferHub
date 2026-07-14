using System.Globalization;
using System.Text.Json;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Ingestion;

public sealed record IngestRequest(
    byte[] Content,
    string? DocumentId = null,
    string? ContentType = null,
    string? FileName = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? EmbeddingModel = null);

/// <summary>
/// extract → chunk → batch → embed on the fleet → upsert. The coordinator grows no embedding path
/// of its own (D6): every vector here comes back from a node through <see cref="IEmbeddingDispatcher"/>,
/// the same one that serves <c>/api/embed</c>.
/// </summary>
public sealed class IngestionPipeline(
    IVectorStore store,
    DocumentIndex documents,
    TextExtractor extractor,
    IEmbeddingDispatcher embeddings,
    IOptions<IngestionOptions> options,
    IOptions<VectorStoreOptions> vectorOptions,
    Metrics metrics,
    ILogger<IngestionPipeline> logger)
{
    public async Task<IngestResult> IngestAsync(string collection, IngestRequest request, CancellationToken cancellationToken)
    {
        var opts = options.Value;

        if (request.Content.LongLength > opts.MaxDocumentBytes)
        {
            throw new DocumentTooLargeException(request.Content.LongLength, opts.MaxDocumentBytes);
        }

        // Fail before doing any work if the collection isn't there. Auto-creating one would mean
        // guessing its dimension from whichever model happened to embed the first chunk, and would
        // route around the admin scope that owns collection lifecycle.
        _ = await store.GetCollectionAsync(collection, cancellationToken)
            ?? throw new KeyNotFoundException($"collection '{collection}' does not exist");

        var documentId = ResolveDocumentId(request);
        var contentHash = DocumentIndex.ContentHash(request.Content);
        var model = ResolveModel(request.EmbeddingModel);

        var existing = await documents.GetAsync(collection, documentId, cancellationToken);
        if (existing is not null
            && string.Equals(existing.ContentHash, contentHash, StringComparison.Ordinal)
            && existing.Status != IngestResult.Partial)
        {
            // Identical bytes, and last time they landed whole. Do no work and say so — the point
            // of the hash. (A `partial` document with the same hash falls through and is retried:
            // "you already have this" would be a lie about a document that is half-missing.)
            logger.LogInformation(
                "Document '{DocumentId}' in '{Collection}' is unchanged ({Chunks} chunks); skipping ingest",
                documentId, collection, existing.Chunks);
            return new IngestResult(documentId, collection, IngestResult.Unchanged,
                existing.Chunks, 0, existing.Bytes, contentHash);
        }

        var extracted = extractor.Extract(request.Content, request.ContentType, request.FileName);
        var chunks = new Chunker(opts).Chunk(extracted);
        if (chunks.Count == 0)
        {
            throw new ExtractionFailedException($"document '{documentId}' produced no chunks");
        }

        var ingestedAt = DateTimeOffset.UtcNow;
        var embedded = 0;
        string? failure = null;

        // Bounded fan-out: at most EmbeddingBatchSize chunks in flight, so a 300-page PDF queues
        // behind itself instead of filling the fleet's job queues and starving interactive chat.
        foreach (var batch in chunks.Chunk(opts.EmbeddingBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                embedded += await EmbedAndUpsertBatchAsync(
                    collection, documentId, contentHash, extracted, chunks.Count,
                    request, model, ingestedAt, batch, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Stop at the first batch that will not go. Grinding through the rest would leave
                // a document with holes scattered through it rather than a clean prefix, and the
                // caller has to be told either way.
                failure = ex.Message;
                metrics.RecordIngestionFailure(collection);
                logger.LogError(ex,
                    "Ingest of '{DocumentId}' into '{Collection}' failed after {Embedded}/{Total} chunks",
                    documentId, collection, embedded, chunks.Count);
                break;
            }
        }

        if (failure is null)
        {
            await DeleteStaleChunksAsync(collection, documentId, chunks.Count, cancellationToken);
            metrics.RecordDocumentIngested(collection, model);
        }

        metrics.RecordChunksEmbedded(collection, embedded);

        var status = failure is null ? IngestResult.Ingested : IngestResult.Partial;
        return new IngestResult(
            documentId, collection, status, chunks.Count, embedded,
            request.Content.LongLength, contentHash, failure);
    }

    private async Task<int> EmbedAndUpsertBatchAsync(
        string collection,
        string documentId,
        string contentHash,
        ExtractedDocument extracted,
        int chunkCount,
        IngestRequest request,
        string model,
        DateTimeOffset ingestedAt,
        DocumentChunk[] batch,
        CancellationToken cancellationToken)
    {
        var vectors = await EmbedWithRetryAsync(batch, model, cancellationToken);

        for (var i = 0; i < batch.Length; i++)
        {
            var chunk = batch[i];
            var upsert = new VectorUpsert(
                Id: DocumentIndex.ChunkId(documentId, chunk.Index),
                Vector: vectors[i],
                Payload: BuildPayload(chunk, documentId),
                Metadata: BuildMetadata(chunk, documentId, contentHash, extracted, chunkCount, request, ingestedAt));

            await store.UpsertAsync(collection, upsert, cancellationToken);
        }

        return batch.Length;
    }

    private async Task<float[][]> EmbedWithRetryAsync(DocumentChunk[] batch, string model, CancellationToken cancellationToken)
    {
        var attempts = options.Value.MaxRetriesPerBatch;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // The whole batch goes out at once — this is the "in flight" the batch size bounds.
                var tasks = batch.Select(c => embeddings.EmbedSingleAsync(c.Text, model, cancellationToken));
                return await Task.WhenAll(tasks);
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException && ex is not NoEmbeddingNodeException)
            {
                // A node dying mid-batch is ordinary and worth retrying; "no node advertises this
                // model at all" is not going to fix itself in 400 ms, so it is not retried.
                logger.LogWarning(ex, "Embedding batch failed (attempt {Attempt}/{Attempts}); retrying", attempt, attempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// A revised document that chunks into fewer pieces than its predecessor leaves the tail
    /// chunks of the old version behind — their ids were never overwritten, because those indices
    /// no longer exist. Deterministic ids (D5) make re-ingest idempotent; they do not make it
    /// shrink. This does.
    /// </summary>
    private async Task DeleteStaleChunksAsync(string collection, string documentId, int chunkCount, CancellationToken cancellationToken)
    {
        var live = new HashSet<string>(
            Enumerable.Range(0, chunkCount).Select(i => DocumentIndex.ChunkId(documentId, i)),
            StringComparer.Ordinal);

        var present = await documents.ChunksOfAsync(collection, documentId, cancellationToken);
        foreach (var stale in present.Where(c => !live.Contains(c.Id)))
        {
            await store.DeleteAsync(collection, stale.Id, cancellationToken);
            logger.LogDebug("Removed stale chunk {ChunkId} of '{DocumentId}'", stale.Id, documentId);
        }
    }

    private static JsonElement BuildPayload(DocumentChunk chunk, string documentId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["text"] = chunk.Text,
            ["documentId"] = documentId,
            ["chunkIndex"] = chunk.Index
        };
        if (chunk.Page is { } page) payload["page"] = page;

        return JsonSerializer.SerializeToElement(payload);
    }

    private static Dictionary<string, string> BuildMetadata(
        DocumentChunk chunk,
        string documentId,
        string contentHash,
        ExtractedDocument extracted,
        int chunkCount,
        IngestRequest request,
        DateTimeOffset ingestedAt)
    {
        // Caller metadata first, so the keys the document model is built from cannot be shadowed
        // by an upload that happens to carry a `documentId` of its own.
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request.Metadata is not null)
        {
            foreach (var (key, value) in request.Metadata) metadata[key] = value;
        }

        metadata[ChunkMetadata.DocumentId] = documentId;
        metadata[ChunkMetadata.ChunkIndex] = chunk.Index.ToString(CultureInfo.InvariantCulture);
        metadata[ChunkMetadata.ChunkCount] = chunkCount.ToString(CultureInfo.InvariantCulture);
        metadata[ChunkMetadata.ContentHash] = contentHash;
        metadata[ChunkMetadata.IngestedAt] = ingestedAt.ToString("O", CultureInfo.InvariantCulture);
        metadata[ChunkMetadata.MediaType] = extracted.MediaType;
        metadata[ChunkMetadata.Bytes] = request.Content.LongLength.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(request.FileName)) metadata[ChunkMetadata.Source] = request.FileName;
        if (chunk.Page is { } page) metadata[ChunkMetadata.Page] = page.ToString(CultureInfo.InvariantCulture);

        return metadata;
    }

    private string ResolveModel(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested;
        if (!string.IsNullOrWhiteSpace(options.Value.EmbeddingModel)) return options.Value.EmbeddingModel;
        return vectorOptions.Value.DefaultEmbeddingModel;
    }

    private static string ResolveDocumentId(IngestRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DocumentId)) return request.DocumentId.Trim();
        if (!string.IsNullOrWhiteSpace(request.FileName)) return Path.GetFileName(request.FileName).Trim();
        return Guid.NewGuid().ToString("n");
    }
}

public sealed class DocumentTooLargeException(long bytes, long limit)
    : InvalidOperationException($"document is {bytes} bytes; the limit is {limit} (Ingestion:MaxDocumentBytes)")
{
    public long Bytes { get; } = bytes;
    public long Limit { get; } = limit;
}
