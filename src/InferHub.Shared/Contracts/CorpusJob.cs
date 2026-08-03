using System.Text.Json.Serialization;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Work the coordinator hands to the node that <em>owns</em> a collection (phase 44, D5): ingest this
/// document, search this corpus.
/// </summary>
/// <remarks>
/// <para>
/// <b>The client-facing API stays the hub's, even though the data lives on the node.</b> A client
/// posts a document to <c>/api/collections/{c}/documents</c> exactly as it always has; the hub sees a
/// node-owned collection, dispatches this job down the connection the node already opened, and the
/// node runs the <em>shared</em> <see cref="IngestionPipeline"/> against its own store. One API for
/// clients, no new surface, no second ingestion path — and the hub keeps enforcing client scoping
/// (phase-31 D2) over data it does not hold.
/// </para>
/// <para>
/// These ride <see cref="InferenceJob"/> like <c>vector-query</c> does, which is design rule 6's
/// bounding read correctly: the rule is about <em>inference</em> jobs carrying Ollama JSON, and this
/// is not inference. It is the same channel, with its own contract — phase-40 D3, again.
/// </para>
/// </remarks>
public sealed record CorpusIngestJob(
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("request")] IngestRequest Request);

public sealed record CorpusIngestResponse(
    [property: JsonPropertyName("result")] IngestResult Result);

/// <summary>A search against a node-owned corpus, run by its owner and returned to the hub.</summary>
public sealed record CorpusSearchJob(
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("k")] int? K = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("rerank")] bool? Rerank = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel = null);

public sealed record CorpusSearchResponse(
    [property: JsonPropertyName("matches")] IReadOnlyList<VectorMatch> Matches);

/// <summary>The job kinds phase 44 adds to the node-facing channel.</summary>
public static class CorpusJobKinds
{
    public const string Ingest = "corpus-ingest";

    public const string Search = "corpus-search";
}
