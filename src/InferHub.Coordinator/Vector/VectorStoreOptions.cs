using System.Text.RegularExpressions;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    public bool Enabled { get; set; } = false;

    /// <summary>Storage backend for the vector store: <c>local</c> (default) or <c>postgres</c>. Case-insensitive.</summary>
    public string Provider { get; set; } = VectorStoreProviderExtensions.Local;

    public string DataDirectory { get; set; } = "./data/vectors";

    public string Distance { get; set; } = "cosine";

    public int ReplicationFactor { get; set; } = 2;

    public string DefaultEmbeddingModel { get; set; } = "nomic-embed-text";

    public int SnapshotEveryOps { get; set; } = 5000;

    public RetrievalOptions Retrieval { get; set; } = new();

    public HealingOptions Healing { get; set; } = new();

    public PostgresStoreOptions Postgres { get; set; } = new();
}

/// <summary>
/// PostgreSQL + pgvector provider settings. Inert unless <see cref="VectorStoreOptions.Provider"/>
/// is <c>postgres</c>. Never commit <see cref="ConnectionString"/> to appsettings.json — set it
/// via env (<c>VectorStore__Postgres__ConnectionString</c>) or user-secrets.
/// </summary>
public sealed class PostgresStoreOptions
{
    /// <summary>Npgsql connection string. Required when the provider is postgres.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Schema holding the per-collection tables and the registry table.</summary>
    public string Schema { get; set; } = "inferhub";

    /// <summary>Prefix for per-collection tables.</summary>
    public string TablePrefix { get; set; } = "vec_";

    /// <summary>Run <c>CREATE EXTENSION IF NOT EXISTS vector</c> at startup.</summary>
    public bool AutoCreateExtension { get; set; } = true;

    /// <summary>Run <c>CREATE SCHEMA IF NOT EXISTS</c> at startup.</summary>
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>ANN index kind: <c>hnsw</c> | <c>ivfflat</c> | <c>none</c> (exact scan).</summary>
    public string Index { get; set; } = "hnsw";

    /// <summary>HNSW <c>m</c> build parameter.</summary>
    public int HnswM { get; set; } = 16;

    /// <summary>HNSW <c>ef_construction</c> build parameter.</summary>
    public int HnswEfConstruction { get; set; } = 64;

    /// <summary>Per-query <c>hnsw.ef_search</c>. Higher = better recall, slower.</summary>
    public int EfSearch { get; set; } = 40;

    /// <summary>Npgsql command timeout, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Max pool size, passed to the data source builder if the connection string omits it.</summary>
    public int MaxPoolSize { get; set; } = 20;
}

public sealed class HealingOptions
{
    /// <summary>Debounce window for fleet-change events; collapses bursts into a single heal pass.</summary>
    public int DebounceMilliseconds { get; set; } = 750;

    /// <summary>Idle interval at which the under-replicated gauge is refreshed even when nothing changes.</summary>
    public int IdleSweepSeconds { get; set; } = 15;
}

public sealed class RetrievalOptions
{
    public const string DefaultTemplate =
        "Use the following context to answer the user's question. " +
        "If the answer is not in the context, say so.\n\n{context}";

    public int DefaultK { get; set; } = 4;

    public int MaxRecords { get; set; } = 8;

    public string OnMissing { get; set; } = "error";

    /// <summary>
    /// Prompt template applied when retrieval is triggered. The literal token
    /// <c>{context}</c> is replaced by the concatenated retrieved records
    /// (each rendered as <c>[id] text</c>, one per line).
    /// </summary>
    public string Template { get; set; } = DefaultTemplate;
}

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

        var isPostgres = VectorStoreProviderExtensions.TryParse(options.Provider, out var provider)
            && provider == VectorStoreProvider.Postgres;

        if (!VectorStoreProviderExtensions.TryParse(options.Provider, out _))
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Provider)} must be one of 'local', 'postgres' (got '{options.Provider}').");
        }

        // DataDirectory only backs the local provider; postgres owns its own durability.
        if (!isPostgres && string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add($"{VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.DataDirectory)} must be set when {VectorStoreOptions.SectionName}:{nameof(VectorStoreOptions.Enabled)} is true and Provider=local.");
        }

        if (isPostgres)
        {
            ValidatePostgres(options.Postgres, failures);
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
}
