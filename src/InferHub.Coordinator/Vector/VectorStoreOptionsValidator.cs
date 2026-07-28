using System.Text.RegularExpressions;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

public sealed partial class VectorStoreOptionsValidator : IValidateOptions<VectorStoreOptions>
{
    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex SqlIdentifierRegex();

    public ValidateOptionsResult Validate(string? name, VectorStoreOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        var known = VectorStoreProviderExtensions.TryParse(options.Provider, out var provider);
        if (!known)
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Provider)} must be one of 'local', 'postgres', 'qdrant' (got '{options.Provider}').");
        }

        // DataDirectory only backs the local provider; an external provider owns its own durability.
        if (known && provider == VectorStoreProvider.Local && string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.DataDirectory)} must be set when {VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Enabled)} is true and Provider=local.");
        }

        if (known && provider == VectorStoreProvider.Postgres)
        {
            ValidatePostgres(options.Postgres, failures);
        }

        if (known && provider == VectorStoreProvider.Qdrant)
        {
            ValidateQdrant(options.Qdrant, failures);
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

            if (string.IsNullOrWhiteSpace(options.Retrieval.Template))
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.Template)} must be set.");
            }
            else if (!options.Retrieval.Template.Contains("{context}", StringComparison.Ordinal))
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.Template)} must contain the literal '{{context}}' placeholder.");
            }

            if (!RetrievalModes.TryParse(options.Retrieval.Mode, out _))
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.Mode)} must be 'vector', 'keyword' or 'hybrid' (got '{options.Retrieval.Mode}').");
            }

            if (options.Retrieval.CandidatesPerBranch < 1)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.CandidatesPerBranch)} must be >= 1 (got {options.Retrieval.CandidatesPerBranch}).");
            }

            if (options.Retrieval.Rerank is not "none" and not "llm")
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.Rerank)} must be 'none' or 'llm' (got '{options.Retrieval.Rerank}').");
            }

            if (options.Retrieval.RerankCandidates < 1)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.RerankCandidates)} must be >= 1 (got {options.Retrieval.RerankCandidates}).");
            }

            if (options.Retrieval.RerankTimeoutSeconds < 1)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Retrieval:{nameof(RetrievalOptions.RerankTimeoutSeconds)} must be >= 1 (got {options.Retrieval.RerankTimeoutSeconds}).");
            }
        }

        if (options.Healing is null)
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Healing)} must not be null.");
        }
        else
        {
            if (options.Healing.DebounceMilliseconds < 50)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Healing:{nameof(HealingOptions.DebounceMilliseconds)} must be >= 50 (got {options.Healing.DebounceMilliseconds}).");
            }

            if (options.Healing.IdleSweepSeconds < 1)
            {
                failures.Add($"{VectorStoreOptions.SectionName}:Healing:{nameof(HealingOptions.IdleSweepSeconds)} must be >= 1 (got {options.Healing.IdleSweepSeconds}).");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidatePostgres(PostgresStoreOptions pg, List<string> failures)
    {
        const string prefix = VectorStoreOptions.SectionName + ":Postgres:";

        if (string.IsNullOrWhiteSpace(pg.ConnectionString))
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.ConnectionString)} must be set when Provider=postgres (set it via env or user-secrets, never appsettings.json).");
        }

        if (string.IsNullOrEmpty(pg.Schema) || !SqlIdentifierRegex().IsMatch(pg.Schema))
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.Schema)} must match ^[a-z_][a-z0-9_]*$ (got '{pg.Schema}').");
        }

        if (string.IsNullOrEmpty(pg.TablePrefix) || !SqlIdentifierRegex().IsMatch(pg.TablePrefix))
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.TablePrefix)} must match ^[a-z_][a-z0-9_]*$ (got '{pg.TablePrefix}').");
        }

        if (pg.Index is not ("hnsw" or "ivfflat" or "none"))
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.Index)} must be one of 'hnsw', 'ivfflat', 'none' (got '{pg.Index}').");
        }

        if (pg.HnswM < 2)
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.HnswM)} must be >= 2 (got {pg.HnswM}).");
        }

        if (pg.HnswEfConstruction < pg.HnswM)
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.HnswEfConstruction)} must be >= HnswM ({pg.HnswM}, got {pg.HnswEfConstruction}).");
        }

        if (pg.EfSearch < 1)
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.EfSearch)} must be >= 1 (got {pg.EfSearch}).");
        }

        if (pg.CommandTimeoutSeconds < 1)
        {
            failures.Add($"{prefix}{nameof(PostgresStoreOptions.CommandTimeoutSeconds)} must be >= 1 (got {pg.CommandTimeoutSeconds}).");
        }
    }

    private static void ValidateQdrant(QdrantStoreOptions q, List<string> failures)
    {
        const string prefix = VectorStoreOptions.SectionName + ":Qdrant:";

        if (string.IsNullOrWhiteSpace(q.Url))
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.Url)} must be set when Provider=qdrant (e.g. http://localhost:6333).");
        }
        else if (!Uri.TryCreate(q.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.Url)} must be an absolute http(s) URL (got '{q.Url}').");
        }

        if (q.TimeoutSeconds < 1)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.TimeoutSeconds)} must be >= 1 (got {q.TimeoutSeconds}).");
        }

        if (string.IsNullOrEmpty(q.CollectionPrefix) || !SqlIdentifierRegex().IsMatch(q.CollectionPrefix))
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.CollectionPrefix)} must match ^[a-z_][a-z0-9_]*$ (got '{q.CollectionPrefix}').");
        }

        if (q.UpsertBatchSize < 1)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.UpsertBatchSize)} must be >= 1 (got {q.UpsertBatchSize}).");
        }

        if (q.OverFetchMultiplier < 1)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.OverFetchMultiplier)} must be >= 1 (got {q.OverFetchMultiplier}).");
        }

        if (q.HnswM < 2)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.HnswM)} must be >= 2 (got {q.HnswM}).");
        }

        if (q.HnswEfConstruct < q.HnswM)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.HnswEfConstruct)} must be >= HnswM ({q.HnswM}, got {q.HnswEfConstruct}).");
        }

        if (q.EfSearch is { } ef && ef < 1)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.EfSearch)} must be >= 1 when set (got {ef}).");
        }

        if (q.Quantization is not ("none" or "scalar" or "binary"))
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.Quantization)} must be one of 'none', 'scalar', 'binary' (got '{q.Quantization}').");
        }

        if (q.PayloadIndexKeys is null)
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.PayloadIndexKeys)} must not be null (use an empty list to index nothing).");
        }
        else if (q.PayloadIndexKeys.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{prefix}{nameof(QdrantStoreOptions.PayloadIndexKeys)} must not contain empty entries.");
        }
    }
}
