using System.Text.RegularExpressions;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Shared.Vector;

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

    /// <summary>
    /// Vector quantization applied to collections created from now on: <c>none</c> (default),
    /// <c>scalar</c> (int8 — roughly 4× less vector memory) or <c>binary</c> (1 bit per dimension —
    /// roughly 32× less, and materially lossy). This is a <b>memory-for-recall trade</b>, not a free
    /// win: quantized vectors rank approximately, so measure the loss on your own corpus with the
    /// eval harness before deciding it is acceptable. Existing collections are untouched.
    /// </summary>
    public string Quantization { get; set; } = "none";

    /// <summary>
    /// Store dense vectors on disk instead of keeping them in RAM. For a collection larger than the
    /// memory you are willing to give it, this is the difference between running and not; the cost
    /// is disk reads on the search path. The HNSW graph stays in memory either way.
    /// </summary>
    public bool OnDisk { get; set; } = false;

    /// <summary>
    /// Metadata keys to build a Qdrant payload index on when a collection is created. Ingestion's
    /// document scans and filtered deletes all filter on <c>documentId</c>, and an unindexed payload
    /// filter is a full scan — so that is the default. Names are InferHub metadata keys; the
    /// connector indexes the reserved payload path it stores them under.
    /// </summary>
    public IList<string> PayloadIndexKeys { get; set; } = ["documentId"];

    /// <summary>
    /// A copy with the address and the credential replaced — how a node projects a corpus the hub
    /// assigned it (phase 44) without mutating the options object its own configuration is bound to,
    /// which would write a resolved secret into the live options graph.
    /// </summary>
    /// <remarks>
    /// It lives here, beside the properties, on purpose: a knob added above and forgotten here would
    /// be a node quietly running different HNSW or quantization settings from the hub's — the kind of
    /// divergence that shows up as "retrieval is worse on that box" months later.
    /// </remarks>
    public QdrantStoreOptions WithConnection(string? url, string? apiKey) => new()
    {
        Url = string.IsNullOrWhiteSpace(url) ? Url : url!,
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? ApiKey : apiKey,
        TimeoutSeconds = TimeoutSeconds,
        CollectionPrefix = CollectionPrefix,
        UpsertBatchSize = UpsertBatchSize,
        OverFetchMultiplier = OverFetchMultiplier,
        HnswM = HnswM,
        HnswEfConstruct = HnswEfConstruct,
        EfSearch = EfSearch,
        Quantization = Quantization,
        OnDisk = OnDisk,
        PayloadIndexKeys = new List<string>(PayloadIndexKeys)
    };
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

