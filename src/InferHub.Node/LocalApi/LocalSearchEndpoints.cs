using System.Text.Json.Serialization;
using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The retrieval query playground on a standalone node (phase 38) — the hub's
/// <c>POST /api/collections/{collection}/search</c>, request and response shapes included.
/// </summary>
/// <remarks>
/// It matters more here than on the hub. A solo operator has no console, no <c>/metrics</c> and no
/// fleet view (phase-37 D5); when a corpus retrieves badly, this endpoint is the only thing that
/// shows *what* came back and in what order.
/// </remarks>
internal static class LocalSearchEndpoints
{
    public static IEndpointRouteBuilder MapLocalSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/collections/{collection}/search", SearchAsync);
        return app;
    }

    private static async Task<IResult> SearchAsync(
        string collection,
        SearchQuery query,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Phase-44 D3: the route is always here; the corpus behind it may not be. The lease is held
        // for the whole search, so a profile that stops the corpus mid-query drains rather than
        // faults.
        using var lease = LocalApiEndpoints.LeaseCorpus(httpContext);

        if (lease is null)
        {
            return LocalApiEndpoints.NoCorpus();
        }

        var store = lease.Corpus.Store;
        var pipeline = lease.Corpus.Retrieval;

        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return Error(StatusCodes.Status400BadRequest, "query is required");
        }

        // A missing collection is a plain 404 here, decided before any embedding runs — the RAG
        // path's OnMissing (error vs passthrough) is a chat-request policy, not a playground one.
        if (await store.GetCollectionAsync(collection, cancellationToken) is null)
        {
            return Error(StatusCodes.Status404NotFound, $"collection '{collection}' does not exist");
        }

        if (query.Mode is not null && !RetrievalModes.TryParse(query.Mode, out _))
        {
            return Error(StatusCodes.Status400BadRequest, $"invalid mode '{query.Mode}'; expected vector, keyword or hybrid");
        }

        var retrieval = new RetrievalRequest(collection, query.K, query.EmbeddingModel, query.Mode, query.Rerank);

        IReadOnlyList<VectorMatch>? matches;
        try
        {
            matches = await pipeline.SearchAsync(retrieval, query.Query!, query.Model, cancellationToken);
        }
        catch (RetrievalUnavailableException ex)
        {
            return Error(StatusCodes.Status424FailedDependency, ex.Message);
        }

        var hits = (matches ?? Array.Empty<VectorMatch>()).Select(ToHit).ToArray();
        return Results.Json(new SearchResponse(collection, query.Mode ?? "vector", hits), LocalApiEndpoints.JsonOptions);
    }

    private static SearchHit ToHit(VectorMatch match)
    {
        string? documentId = null;
        int? page = null;
        if (match.Metadata is { } metadata)
        {
            metadata.TryGetValue("documentId", out documentId);
            if (metadata.TryGetValue("page", out var rawPage) && int.TryParse(rawPage, out var parsed))
            {
                page = parsed;
            }
        }

        var text = ChunkText.Extract(match.Payload);
        var snippet = text.Length <= 280 ? text : text[..280];
        return new SearchHit(match.Id, match.Score, documentId, page, snippet);
    }

    private static IResult Error(int statusCode, string message)
        => Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: statusCode);

    public sealed record SearchQuery(
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("mode")] string? Mode = null,
        [property: JsonPropertyName("k")] int? K = null,
        [property: JsonPropertyName("rerank")] bool? Rerank = null,
        [property: JsonPropertyName("model")] string? Model = null,
        [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel = null);

    private sealed record SearchResponse(string Collection, string Mode, IReadOnlyList<SearchHit> Hits);

    private sealed record SearchHit(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("documentId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DocumentId,
        [property: JsonPropertyName("page"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Page,
        [property: JsonPropertyName("text")] string Text);
}
