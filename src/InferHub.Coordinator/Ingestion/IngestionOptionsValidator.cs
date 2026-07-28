using InferHub.Shared.Ingestion;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Ingestion;

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
