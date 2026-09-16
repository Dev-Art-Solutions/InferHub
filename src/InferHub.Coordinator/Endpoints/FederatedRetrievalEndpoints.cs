using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Phase 75. <c>POST /api/retrieve/federated</c> runs the same single-collection search
/// <see cref="SearchEndpoints"/> already does — once per named collection, unchanged — in parallel,
/// and fuses the ranked lists into one. Every name still has exactly one authority, looked up exactly
/// as <c>/search</c> looks it up (44 D1); this endpoint is a caller of that lookup, not a second one.
/// </summary>
public static class FederatedRetrievalEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Fan-out has no natural bound otherwise (75 D5) — raise with an argument, not a config knob.</summary>
    private const int MaxCollections = 16;

    private const int DefaultK = 10;

    private const int DefaultMaxWaitMs = 4000;

    public static IEndpointRouteBuilder MapFederatedRetrievalEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/retrieve/federated", SearchAsync);
        return app;
    }

    private static async Task<IResult> SearchAsync(
        FederatedSearchQuery query,
        HttpContext context,
        IVectorStore store,
        RetrievalPipeline pipeline,
        NodeCorpusDispatcher corpora,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
        {
            return Error(StatusCodes.Status400BadRequest, "query is required");
        }

        if (query.Mode is not null && !RetrievalModes.TryParse(query.Mode, out _))
        {
            return Error(StatusCodes.Status400BadRequest, $"invalid mode '{query.Mode}'; expected vector, keyword or hybrid");
        }

        var names = (query.Collections ?? Array.Empty<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            return Error(StatusCodes.Status400BadRequest, "collections must name at least one collection");
        }

        if (names.Length > MaxCollections)
        {
            return Error(StatusCodes.Status400BadRequest, $"collections names {names.Length}; the fan-out ceiling is {MaxCollections}");
        }

        var k = query.K is > 0 ? query.K.Value : DefaultK;
        var perCollectionK = query.PerCollectionK is > 0 ? query.PerCollectionK.Value : k;
        var maxWait = TimeSpan.FromMilliseconds(query.MaxWaitMs is > 0 ? query.MaxWaitMs.Value : DefaultMaxWaitMs);

        var results = await Task.WhenAll(names.Select(name => SearchOneAsync(
            name, query, perCollectionK, maxWait, store, pipeline, corpora, context, cancellationToken)));

        var fused = FuseAcrossCollections(results, k);
        var sources = results
            .Select(result => result.Source)
            .OrderBy(source => source.Collection, StringComparer.Ordinal)
            .ToArray();

        return Results.Json(new FederatedSearchResponse(fused, sources), JsonOptions);
    }

    /// <summary>
    /// Exactly the single-collection path (75 D1): node-owned goes to its owner (44 D5), hub-owned
    /// goes through the same <see cref="RetrievalPipeline"/> the playground uses. The only things this
    /// method adds are the per-name scope check (D3) and the per-target timeout (D4).
    /// </summary>
    private static async Task<CollectionResult> SearchOneAsync(
        string collection,
        FederatedSearchQuery query,
        int k,
        TimeSpan maxWait,
        IVectorStore store,
        RetrievalPipeline pipeline,
        NodeCorpusDispatcher corpora,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // 31 D3's principle, applied per name instead of per route: the check runs before the store or
        // the owner is ever asked, so a name outside the caller's scope reads identically to one that
        // does not exist — the fan-out must not leak either fact through a status field.
        if (!CollectionAccessPolicy.CanAccess(context, collection))
        {
            return CollectionResult.Empty(collection, "not_found");
        }

        var started = Stopwatch.GetTimestamp();
        using var perTargetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perTargetCts.CancelAfter(maxWait);

        try
        {
            IReadOnlyList<VectorMatch> matches;

            if (corpora.IsNodeOwned(collection))
            {
                matches = await corpora.SearchAsync(
                    new CorpusSearchJob(collection, query.Query!, k, query.Mode, query.Rerank, query.Model, query.EmbeddingModel),
                    perTargetCts.Token);
            }
            else
            {
                // A missing collection is decided before any embedding runs, same as /search.
                if (await store.GetCollectionAsync(collection, perTargetCts.Token) is null)
                {
                    return CollectionResult.Empty(collection, "not_found");
                }

                var retrieval = new RetrievalRequest(collection, k, query.EmbeddingModel, query.Mode, query.Rerank);
                matches = await pipeline.SearchAsync(retrieval, query.Query!, query.Model, perTargetCts.Token)
                    ?? Array.Empty<VectorMatch>();
            }

            return new CollectionResult(
                collection,
                matches,
                new FederatedSourceResult(collection, "ok", matches.Count, ElapsedMs(started)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The per-target budget expired, not the caller's own request (D4): answer with whoever
            // did answer in time rather than failing the whole call over one slow corpus.
            return CollectionResult.Empty(collection, "timeout", ElapsedMs(started));
        }
        catch (NodeCorpusUnavailableException)
        {
            // The owner is not connected — 31 D4's failure mode, named per source rather than falling
            // back to a different corpus or dropping the name silently.
            return CollectionResult.Empty(collection, "unavailable", ElapsedMs(started));
        }
        catch (RetrievalUnavailableException)
        {
            return CollectionResult.Empty(collection, "unavailable", ElapsedMs(started));
        }
    }

    /// <summary>
    /// Reciprocal Rank Fusion across collections (75 D2). <see cref="HybridSearch.Fuse"/> already does
    /// RRF but keys on the record id alone, which is safe within one collection's own namespace and
    /// unsafe across several — two unrelated collections' chunk "1" are not the same record. This keys
    /// on <c>(collection, id)</c> instead, reusing <see cref="HybridSearch.RrfK"/> for the same
    /// different-engines-different-scales reasoning phase 24 already argued.
    /// </summary>
    internal static IReadOnlyList<FederatedMatch> FuseAcrossCollections(
        IReadOnlyList<CollectionResult> results,
        int k)
    {
        if (k < 1) return Array.Empty<FederatedMatch>();

        var scores = new Dictionary<(string Collection, string Id), double>();
        var records = new Dictionary<(string Collection, string Id), FederatedMatch>();

        foreach (var result in results)
        {
            for (var i = 0; i < result.Matches.Count; i++)
            {
                var match = result.Matches[i];
                var key = (result.Collection, match.Id);
                var contribution = 1.0 / (HybridSearch.RrfK + i + 1);
                scores[key] = scores.TryGetValue(key, out var running) ? running + contribution : contribution;
                records.TryAdd(key, new FederatedMatch(result.Collection, match.Id, 0, match.Payload, match.Metadata));
            }
        }

        return scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key.Collection, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Id, StringComparer.Ordinal)
            .Take(k)
            .Select(pair => records[pair.Key] with { Score = pair.Value })
            .ToArray();
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, statusCode: statusCode);

    public sealed record FederatedSearchQuery(
        [property: JsonPropertyName("collections")] IReadOnlyList<string>? Collections,
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("mode")] string? Mode = null,
        [property: JsonPropertyName("k")] int? K = null,
        [property: JsonPropertyName("perCollectionK")] int? PerCollectionK = null,
        [property: JsonPropertyName("maxWaitMs")] int? MaxWaitMs = null,
        [property: JsonPropertyName("rerank")] bool? Rerank = null,
        [property: JsonPropertyName("model")] string? Model = null,
        [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel = null);

    public sealed record FederatedSearchResponse(
        [property: JsonPropertyName("matches")] IReadOnlyList<FederatedMatch> Matches,
        [property: JsonPropertyName("sources")] IReadOnlyList<FederatedSourceResult> Sources);

    public sealed record FederatedMatch(
        [property: JsonPropertyName("collection")] string Collection,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("payload")] JsonElement? Payload,
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

    /// <summary>One collection's contribution: whether it answered, how many matches, how long it took.</summary>
    public sealed record FederatedSourceResult(
        [property: JsonPropertyName("collection")] string Collection,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("matches")] int Matches,
        [property: JsonPropertyName("elapsedMs")] long ElapsedMs);

    internal sealed record CollectionResult(string Collection, IReadOnlyList<VectorMatch> Matches, FederatedSourceResult Source)
    {
        public static CollectionResult Empty(string collection, string status, long elapsedMs = 0) =>
            new(collection, Array.Empty<VectorMatch>(), new FederatedSourceResult(collection, status, 0, elapsedMs));
    }
}
