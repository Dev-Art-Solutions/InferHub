using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// The retrieval query playground (phase 24). <c>POST /api/collections/{collection}/search</c> runs a
/// query in a chosen mode and returns the ranked chunks — the same retrieval the RAG path would do,
/// but visible. Client-scoped like ingestion (it reads a collection a client owns), so it rides the
/// same bearer guard as <c>/api/collections/...</c> and never needs an admin key.
/// </summary>
public static class SearchEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/collections/{collection}/search", SearchAsync).RequireCollectionScope();
        return app;
    }

    private static async Task<IResult> SearchAsync(
        string collection,
        SearchQuery query,
        IVectorStore store,
        RetrievalPipeline pipeline,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return Results.Json(new { error = "query is required" }, JsonOptions, statusCode: StatusCodes.Status400BadRequest);
        }

        // A missing collection is a plain 404 here, decided before any embedding runs — the RAG
        // path's OnMissing (error vs passthrough) is a chat-request policy, not a playground one.
        if (await store.GetCollectionAsync(collection, cancellationToken) is null)
        {
            return Results.Json(new { error = $"collection '{collection}' does not exist" }, JsonOptions, statusCode: StatusCodes.Status404NotFound);
        }

        if (query.Mode is not null && !RetrievalModes.TryParse(query.Mode, out _))
        {
            return Results.Json(new { error = $"invalid mode '{query.Mode}'; expected vector, keyword or hybrid" }, JsonOptions, statusCode: StatusCodes.Status400BadRequest);
        }

        var retrieval = new RetrievalRequest(collection, query.K, query.EmbeddingModel, query.Mode, query.Rerank);

        IReadOnlyList<VectorMatch>? matches;
        try
        {
            matches = await pipeline.SearchAsync(retrieval, query.Query!, query.Model, cancellationToken);
        }
        catch (RetrievalUnavailableException ex)
        {
            return Results.Json(new { error = ex.Message }, JsonOptions, statusCode: StatusCodes.Status424FailedDependency);
        }

        var hits = (matches ?? Array.Empty<VectorMatch>()).Select(ToHit).ToArray();
        return Results.Json(new SearchResponse(collection, query.Mode ?? "vector", hits), JsonOptions);
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
