using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Ingestion;

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

public sealed class IngestionOptionsValidator : IValidateOptions<IngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, IngestionOptions options)
    {
        const string prefix = IngestionOptions.SectionName + ":";
        var failures = new List<string>();

        if (options.MaxChars < 64)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.MaxChars)} must be >= 64 (got {options.MaxChars}).");
        }

        // Overlap at or above the chunk size means chunk N+1 starts at or before chunk N did:
        // the chunker would never advance and a 1 MB document would spin forever.
        if (options.OverlapChars < 0 || options.OverlapChars >= options.MaxChars)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.OverlapChars)} must be >= 0 and < MaxChars ({options.MaxChars}, got {options.OverlapChars}).");
        }

        if (options.MaxDocumentBytes < 1)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.MaxDocumentBytes)} must be >= 1 (got {options.MaxDocumentBytes}).");
        }

        if (options.EmbeddingBatchSize < 1)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.EmbeddingBatchSize)} must be >= 1 (got {options.EmbeddingBatchSize}).");
        }

        if (options.MaxRetriesPerBatch < 1)
        {
            failures.Add($"{prefix}{nameof(IngestionOptions.MaxRetriesPerBatch)} must be >= 1 (got {options.MaxRetriesPerBatch}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
