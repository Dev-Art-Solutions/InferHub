using System.Text.RegularExpressions;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    public bool Enabled { get; set; } = false;

    /// <summary>Storage backend for the vector store: <c>local</c> (default), <c>postgres</c> or <c>qdrant</c>. Case-insensitive.</summary>
    public string Provider { get; set; } = VectorStoreProviderExtensions.Local;

    public string DataDirectory { get; set; } = "./data/vectors";

    public string Distance { get; set; } = "cosine";

    public int ReplicationFactor { get; set; } = 2;

    public string DefaultEmbeddingModel { get; set; } = "nomic-embed-text";

    public int SnapshotEveryOps { get; set; } = 5000;

    public RetrievalOptions Retrieval { get; set; } = new();

    public HealingOptions Healing { get; set; } = new();

    public PostgresStoreOptions Postgres { get; set; } = new();

    public QdrantStoreOptions Qdrant { get; set; } = new();
}

/// <summary>
/// Qdrant provider settings. Inert unless <see cref="VectorStoreOptions.Provider"/> is <c>qdrant</c>.
/// The connector speaks Qdrant's JSON REST API by hand over <see cref="System.Net.Http.HttpClient"/> —
/// no client package, no gRPC — so this provider adds no dependency. Never commit
/// <see cref="ApiKey"/> to appsettings.json; set it via env (<c>VectorStore__Qdrant__ApiKey</c>) or
/// user-secrets.
/// </summary>
public sealed class QdrantStoreOptions
{
    /// <summary>Base URL of the Qdrant REST API, e.g. <c>http://localhost:6333</c>. Required when the provider is qdrant.</summary>
    public string Url { get; set; } = "";

    /// <summary>Qdrant API key, sent as the <c>api-key</c> header. Null/empty for a local, unauthenticated Qdrant.</summary>
    public string? ApiKey { get; set; }

    /// <summary>HTTP timeout, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Prefix applied to the InferHub collection name to form the Qdrant collection name, so a shared
    /// Qdrant can host InferHub collections alongside another app's without a clash.
    /// </summary>
    public string CollectionPrefix { get; set; } = "inferhub_";

    /// <summary>How many points are sent per upsert request.</summary>
    public int UpsertBatchSize { get; set; } = 128;

    /// <summary>
    /// When a query carries a metadata filter, an HNSW scan with a selective post-filter can return
    /// fewer than <c>k</c> rows; the connector over-fetches by this multiple and trims to <c>k</c>.
    /// </summary>
    public int OverFetchMultiplier { get; set; } = 4;

    /// <summary>HNSW <c>m</c> build parameter for new collections.</summary>
    public int HnswM { get; set; } = 16;

    /// <summary>HNSW <c>ef_construct</c> build parameter for new collections.</summary>
    public int HnswEfConstruct { get; set; } = 64;

    /// <summary>Per-query HNSW <c>ef</c>. Higher = better recall, slower. Null uses Qdrant's own default.</summary>
    public int? EfSearch { get; set; }
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
    /// Default retrieval mode when a request sends no <c>X-InferHub-Retrieve-Mode</c> header:
    /// <c>vector</c> (default) | <c>keyword</c> | <c>hybrid</c>. The default stays <c>vector</c> so an
    /// existing deployment sees byte-identical results — new capability that silently changes old
    /// answers is a regression wearing a feature's clothes.
    /// </summary>
    public string Mode { get; set; } = "vector";

    /// <summary>
    /// How many candidates each branch (vector, keyword) fetches before RRF fusion in <c>hybrid</c>
    /// mode. Larger recovers more of the long tail at more work; 20 is the usual sweet spot.
    /// </summary>
    public int CandidatesPerBranch { get; set; } = 20;

    /// <summary>
    /// Default reranker: <c>none</c> (default) | <c>llm</c>. Reranking costs a fleet round trip, so it
    /// is off unless a request asks for it with <c>X-InferHub-Rerank: true</c> or this is set to
    /// <c>llm</c>.
    /// </summary>
    public string Rerank { get; set; } = "none";

    /// <summary>Chat model used by the LLM reranker. When null, the request's own model is used.</summary>
    public string? RerankModel { get; set; }

    /// <summary>Upper bound on how many candidates are handed to the reranker in one round trip.</summary>
    public int RerankCandidates { get; set; } = 20;

    /// <summary>
    /// How long the reranker waits for the fleet before giving up and returning the candidates in
    /// their original (un-reranked) order. A reranker that can hang is worse than no reranker.
    /// </summary>
    public int RerankTimeoutSeconds { get; set; } = 20;

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
    }
}
