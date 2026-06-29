using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    public bool Enabled { get; set; } = false;

    public string DataDirectory { get; set; } = "./data/vectors";

    public string Distance { get; set; } = "cosine";

    public int ReplicationFactor { get; set; } = 2;

    public string DefaultEmbeddingModel { get; set; } = "nomic-embed-text";

    public int SnapshotEveryOps { get; set; } = 5000;

    public RetrievalOptions Retrieval { get; set; } = new();
}

public sealed class RetrievalOptions
{
    public int DefaultK { get; set; } = 4;

    public int MaxRecords { get; set; } = 8;

    public string OnMissing { get; set; } = "error";
}

public sealed class VectorStoreOptionsValidator : IValidateOptions<VectorStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, VectorStoreOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.DataDirectory)} must be set when {VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Enabled)} is true.");
        }

        if (!DistanceMetricExtensions.TryParse(options.Distance, out _))
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Distance)} must be one of 'cosine', 'dot', 'l2' (got '{options.Distance}').");
        }

        if (options.ReplicationFactor < 1)
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.ReplicationFactor)} must be >= 1 (got {options.ReplicationFactor}).");
        }

        if (options.SnapshotEveryOps < 1)
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.SnapshotEveryOps)} must be >= 1 (got {options.SnapshotEveryOps}).");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultEmbeddingModel))
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.DefaultEmbeddingModel)} must be set.");
        }

        if (options.Retrieval is null)
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Retrieval)} must not be null.");
        }
        else
        {
            if (options.Retrieval.DefaultK < 1)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.DefaultK)} must be >= 1 (got {options.Retrieval.DefaultK}).");
            }

            if (options.Retrieval.MaxRecords < options.Retrieval.DefaultK)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.MaxRecords)} must be >= DefaultK ({options.Retrieval.DefaultK}, got {options.Retrieval.MaxRecords}).");
            }

            if (options.Retrieval.OnMissing is not "error" and not "passthrough")
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.OnMissing)} must be 'error' or 'passthrough' (got '{options.Retrieval.OnMissing}').");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
