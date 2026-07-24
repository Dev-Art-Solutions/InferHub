using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Coordinator.Vector.Qdrant;

/// <summary>
/// Speaks Qdrant's JSON REST API by hand over an <see cref="HttpClient"/> — no client package, no
/// gRPC. This is the same call the OpenAI upstream client made (phase 22): a wire format that is
/// plain JSON does not need a dependency to talk to, and taking one would drag protobuf into the
/// coordinator for a store that answers REST perfectly well. Nothing here persists or logs content.
/// </summary>
internal sealed class QdrantClient(HttpClient http)
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client this connector drives.</summary>
    public const string HttpClientName = "qdrant";

    /// <summary>Name of the dense (embedding) vector in a hybrid-capable collection (phase 34). A
    /// collection created on 3.1 has a single <em>unnamed</em> dense vector instead; the two shapes
    /// are why every points call carries a <c>named</c> flag.</summary>
    public const string DenseVector = "dense";

    /// <summary>Name of the sparse (lexical) vector in a hybrid-capable collection (phase 34).</summary>
    public const string SparseVector = "sparse";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Points an <see cref="HttpClient"/> at a Qdrant. A trailing slash on the base address matters:
    /// without one, <c>.../qdrant</c> + <c>collections/x</c> silently drops the last path segment.
    /// </summary>
    public static HttpClient Configure(HttpClient http, string url, string? apiKey, int timeoutSeconds)
    {
        http.BaseAddress = new Uri(url.EndsWith('/') ? url : url + "/");
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Qdrant authenticates with a bare `api-key` header, not a Bearer token.
            http.DefaultRequestHeaders.Add("api-key", apiKey);
        }
        return http;
    }

    // ---- collections ------------------------------------------------------------------

    public async Task<bool> CollectionExistsAsync(string qdrantName, CancellationToken cancellationToken)
    {
        var result = await GetAsync<QdrantExists>($"collections/{qdrantName}/exists", cancellationToken);
        return result?.Result?.Exists ?? false;
    }

    /// <summary>Creates a legacy dense-only collection with a single <em>unnamed</em> vector — the
    /// 3.1 shape. Kept for round-tripping collections created before phase 34; new collections go
    /// through <see cref="CreateHybridCollectionAsync"/>.</summary>
    public async Task CreateCollectionAsync(
        string qdrantName, int dimension, DistanceMetric metric, int hnswM, int hnswEfConstruct, CancellationToken cancellationToken)
    {
        var body = new QdrantCreateCollection(
            JsonSerializer.SerializeToElement(new QdrantVectorParams(dimension, ToQdrantDistance(metric)), JsonOptions),
            SparseVectors: null,
            new QdrantHnswConfig(hnswM, hnswEfConstruct));
        await SendAsync(HttpMethod.Put, $"collections/{qdrantName}", body, cancellationToken);
    }

    /// <summary>
    /// Creates a hybrid-capable collection (phase 34): a <b>named</b> dense vector plus a named sparse
    /// vector declared with <c>modifier: idf</c>, so Qdrant applies inverse-document-frequency
    /// weighting to the sparse (lexical) branch server-side. This is the shape every collection
    /// created on 3.2+ takes; a collection created on 3.1 stays dense-only until re-created or
    /// migrated (phase 35).
    /// </summary>
    public async Task CreateHybridCollectionAsync(
        string qdrantName, int dimension, DistanceMetric metric, int hnswM, int hnswEfConstruct, CancellationToken cancellationToken)
    {
        var vectors = JsonSerializer.SerializeToElement(
            new Dictionary<string, QdrantVectorParams>(StringComparer.Ordinal)
            {
                [DenseVector] = new QdrantVectorParams(dimension, ToQdrantDistance(metric))
            },
            JsonOptions);
        var sparse = new Dictionary<string, QdrantSparseParams>(StringComparer.Ordinal)
        {
            [SparseVector] = new QdrantSparseParams("idf")
        };
        var body = new QdrantCreateCollection(vectors, sparse, new QdrantHnswConfig(hnswM, hnswEfConstruct));
        await SendAsync(HttpMethod.Put, $"collections/{qdrantName}", body, cancellationToken);
    }

    /// <summary>
    /// Collection dimension, distance and whether it is hybrid-capable (has a sparse vector), or null
    /// when the collection does not exist. Handles both the 3.1 unnamed-vector shape
    /// (<c>vectors: {size, distance}</c>) and the 3.2 named shape (<c>vectors: {dense: {…}}</c> plus
    /// <c>sparse_vectors: {sparse: {…}}</c>).
    /// </summary>
    public async Task<(int Dimension, string Distance, bool Hybrid)?> GetCollectionAsync(string qdrantName, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<QdrantGetCollection>($"collections/{qdrantName}", cancellationToken, allow404: true);
        var vectors = envelope?.Result?.Config?.Params?.Vectors;
        if (vectors is not { ValueKind: JsonValueKind.Object } v) return null;

        // Unnamed (3.1): the vectors object *is* the params. Named (3.2): it maps names → params, and
        // the dense vector lives under its name.
        JsonElement dense = v.TryGetProperty("size", out _)
            ? v
            : v.TryGetProperty(DenseVector, out var named) ? named : default;
        if (dense.ValueKind != JsonValueKind.Object || !dense.TryGetProperty("size", out var size)) return null;

        var distance = dense.TryGetProperty("distance", out var d) ? d.GetString() ?? "Cosine" : "Cosine";
        var hybrid = envelope!.Result!.Config!.Params!.SparseVectors is { ValueKind: JsonValueKind.Object } sv
                     && sv.TryGetProperty(SparseVector, out _);
        return (size.GetInt32(), distance, hybrid);
    }

    public async Task<long> CountAsync(string qdrantName, QdrantFilter? filter, CancellationToken cancellationToken)
    {
        var body = new QdrantCountRequest(filter, Exact: true);
        var result = await SendAsync<QdrantCountRequest, QdrantCountResponse>(
            HttpMethod.Post, $"collections/{qdrantName}/points/count", body, cancellationToken);
        return result?.Result?.Count ?? 0;
    }

    public async Task<IReadOnlyList<string>> ListCollectionNamesAsync(CancellationToken cancellationToken)
    {
        var result = await GetAsync<QdrantListCollections>("collections", cancellationToken);
        return (result?.Result?.Collections ?? []).Select(c => c.Name).ToArray();
    }

    public async Task DropCollectionAsync(string qdrantName, CancellationToken cancellationToken)
        => await SendAsync(HttpMethod.Delete, $"collections/{qdrantName}", (object?)null, cancellationToken);

    // ---- points -----------------------------------------------------------------------

    /// <summary>
    /// Upsert points. When <paramref name="named"/> (a hybrid-capable collection) each point's vector
    /// is written as <c>{dense: […], sparse: {…}}</c> — the sparse entry omitted for a point with no
    /// lexical text; otherwise (a 3.1 dense-only collection) the vector is a bare array.
    /// </summary>
    public async Task UpsertPointsAsync(string qdrantName, bool named, IReadOnlyList<QdrantPoint> points, CancellationToken cancellationToken)
    {
        var wire = new QdrantWirePoint[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            wire[i] = new QdrantWirePoint(points[i].Id, VectorValue(points[i], named), points[i].Payload);
        }
        var body = new QdrantUpsertRequest(wire);
        await SendAsync(HttpMethod.Put, $"collections/{qdrantName}/points?wait=true", body, cancellationToken);
    }

    private static JsonElement VectorValue(QdrantPoint point, bool named)
    {
        if (!named)
        {
            return JsonSerializer.SerializeToElement(point.Dense, JsonOptions);
        }

        var map = new Dictionary<string, object>(StringComparer.Ordinal) { [DenseVector] = point.Dense };
        if (point.Sparse is { } sparse)
        {
            map[SparseVector] = sparse;
        }
        return JsonSerializer.SerializeToElement(map, JsonOptions);
    }

    public async Task<QdrantRetrievedPoint?> RetrievePointAsync(string qdrantName, string pointId, bool withVector, CancellationToken cancellationToken)
    {
        var body = new QdrantRetrieveRequest([pointId], WithPayload: true, WithVector: withVector);
        var result = await SendAsync<QdrantRetrieveRequest, QdrantRetrieveResponse>(
            HttpMethod.Post, $"collections/{qdrantName}/points", body, cancellationToken);
        return result?.Result?.Count > 0 ? result.Result[0] : null;
    }

    public async Task DeletePointsAsync(string qdrantName, IReadOnlyList<string> pointIds, CancellationToken cancellationToken)
    {
        var body = new QdrantDeletePointsRequest(pointIds);
        await SendAsync(HttpMethod.Post, $"collections/{qdrantName}/points/delete?wait=true", body, cancellationToken);
    }

    public async Task DeleteByFilterAsync(string qdrantName, QdrantFilter filter, CancellationToken cancellationToken)
    {
        var body = new QdrantDeleteByFilterRequest(filter);
        await SendAsync(HttpMethod.Post, $"collections/{qdrantName}/points/delete?wait=true", body, cancellationToken);
    }

    /// <summary>
    /// Plain dense (ANN) search. <paramref name="vectorName"/> is the named dense vector on a
    /// hybrid-capable collection (so the request sends <c>{name, vector}</c>), or null for a 3.1
    /// dense-only collection (a bare <c>vector</c> array).
    /// </summary>
    public async Task<IReadOnlyList<QdrantScoredPoint>> SearchAsync(
        string qdrantName, float[] vector, string? vectorName, int limit, QdrantFilter? filter, int? efSearch, CancellationToken cancellationToken)
    {
        var vectorValue = vectorName is null
            ? JsonSerializer.SerializeToElement(vector, JsonOptions)
            : JsonSerializer.SerializeToElement(new QdrantNamedVector(vectorName, vector), JsonOptions);
        var body = new QdrantSearchRequest(
            vectorValue, limit, filter, WithPayload: true, WithVector: false,
            efSearch is { } ef ? new QdrantSearchParams(ef) : null);
        var result = await SendAsync<QdrantSearchRequest, QdrantSearchResponse>(
            HttpMethod.Post, $"collections/{qdrantName}/points/search", body, cancellationToken);
        return result?.Result ?? [];
    }

    /// <summary>
    /// A single fused query over a hybrid-capable collection (Qdrant Query API): a dense and a sparse
    /// prefetch, combined by reciprocal rank fusion <b>inside Qdrant</b>. One round trip replaces the
    /// hub running two branches and fusing itself.
    /// </summary>
    public async Task<IReadOnlyList<QdrantScoredPoint>> QueryFusedAsync(
        string qdrantName, float[] dense, QdrantSparse sparse, int prefetchLimit, int limit, QdrantFilter? filter, CancellationToken cancellationToken)
    {
        var prefetch = new[]
        {
            new QdrantPrefetch(JsonSerializer.SerializeToElement(dense, JsonOptions), DenseVector, filter, prefetchLimit),
            new QdrantPrefetch(JsonSerializer.SerializeToElement(sparse, JsonOptions), SparseVector, filter, prefetchLimit)
        };
        var body = new QdrantQueryRequest(
            prefetch, JsonSerializer.SerializeToElement(new QdrantFusion("rrf"), JsonOptions),
            Using: null, Filter: null, limit, WithPayload: true, WithVector: false);
        return await QueryAsync(qdrantName, body, cancellationToken);
    }

    /// <summary>A pure sparse (lexical) search over a hybrid-capable collection's sparse vector.</summary>
    public async Task<IReadOnlyList<QdrantScoredPoint>> QuerySparseAsync(
        string qdrantName, QdrantSparse sparse, int limit, QdrantFilter? filter, CancellationToken cancellationToken)
    {
        var body = new QdrantQueryRequest(
            Prefetch: null, JsonSerializer.SerializeToElement(sparse, JsonOptions),
            Using: SparseVector, filter, limit, WithPayload: true, WithVector: false);
        return await QueryAsync(qdrantName, body, cancellationToken);
    }

    private async Task<IReadOnlyList<QdrantScoredPoint>> QueryAsync(string qdrantName, QdrantQueryRequest body, CancellationToken cancellationToken)
    {
        var result = await SendAsync<QdrantQueryRequest, QdrantQueryResponse>(
            HttpMethod.Post, $"collections/{qdrantName}/points/query", body, cancellationToken);
        return result?.Result?.Points ?? [];
    }

    public async Task<(IReadOnlyList<QdrantRetrievedPoint> Points, JsonElement? NextOffset)> ScrollAsync(
        string qdrantName, QdrantFilter? filter, int limit, JsonElement? offset, bool withVector, CancellationToken cancellationToken)
    {
        var body = new QdrantScrollRequest(filter, limit, offset, WithPayload: true, WithVector: withVector);
        var result = await SendAsync<QdrantScrollRequest, QdrantScrollResponse>(
            HttpMethod.Post, $"collections/{qdrantName}/points/scroll", body, cancellationToken);
        return (result?.Result?.Points ?? [], result?.Result?.NextPageOffset);
    }

    // ---- plumbing ---------------------------------------------------------------------

    private async Task<TResponse?> GetAsync<TResponse>(string path, CancellationToken cancellationToken, bool allow404 = false)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        if (allow404 && response.StatusCode == HttpStatusCode.NotFound) return default;
        await ThrowIfUnsuccessfulAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    private async Task SendAsync<TRequest>(HttpMethod method, string path, TRequest? body, CancellationToken cancellationToken)
        => await SendAsync<TRequest, JsonElement>(method, path, body, cancellationToken);

    private async Task<TResponse?> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest? body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            message.Content = JsonContent.Create(body, options: JsonOptions);
        }
        using var response = await http.SendAsync(message, cancellationToken);
        await ThrowIfUnsuccessfulAsync(response, cancellationToken);
        if (response.Content.Headers.ContentLength == 0) return default;
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
    }

    private static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new QdrantException((int)response.StatusCode, Describe(response.StatusCode, raw));
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        var detail = body;
        try
        {
            var envelope = JsonSerializer.Deserialize<QdrantErrorEnvelope>(body, JsonOptions);
            if (!string.IsNullOrWhiteSpace(envelope?.Status?.Error))
            {
                detail = envelope.Status.Error;
            }
        }
        catch (JsonException)
        {
            // Not every failure comes back as a Qdrant status envelope; the raw body will do.
        }

        detail = detail.Trim();
        return detail.Length == 0
            ? $"Qdrant returned {(int)status} {status}"
            : $"Qdrant returned {(int)status} {status}: {detail}";
    }

    internal static string ToQdrantDistance(DistanceMetric metric) => metric switch
    {
        DistanceMetric.Cosine => "Cosine",
        DistanceMetric.Dot => "Dot",
        DistanceMetric.L2 => "Euclid",
        _ => "Cosine"
    };
}

/// <summary>Qdrant answered, and it answered badly. Carries the status it used.</summary>
internal sealed class QdrantException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// ---- wire DTOs (explicit snake_case names; no naming policy is applied) ----------------

// `vectors` is a JsonElement so the same request type serves both shapes: an unnamed
// QdrantVectorParams (3.1 dense-only) and a name→params map (3.2 named dense). `sparse_vectors` is
// present only for hybrid-capable collections.
internal sealed record QdrantCreateCollection(
    [property: JsonPropertyName("vectors")] JsonElement Vectors,
    [property: JsonPropertyName("sparse_vectors")] IReadOnlyDictionary<string, QdrantSparseParams>? SparseVectors,
    [property: JsonPropertyName("hnsw_config")] QdrantHnswConfig HnswConfig);

internal sealed record QdrantVectorParams(
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("distance")] string Distance);

// A sparse vector declared with `modifier: idf` — Qdrant applies inverse-document-frequency
// weighting to it server-side, so the hub ships only raw term frequencies (phase 34).
internal sealed record QdrantSparseParams(
    [property: JsonPropertyName("modifier")] string Modifier);

// Named-vector form of a search query: {"name": "dense", "vector": [...]}.
internal sealed record QdrantNamedVector(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("vector")] float[] Vector);

// A Qdrant sparse vector on the wire: parallel index/value arrays.
internal sealed record QdrantSparse(
    [property: JsonPropertyName("indices")] uint[] Indices,
    [property: JsonPropertyName("values")] float[] Values);

internal sealed record QdrantHnswConfig(
    [property: JsonPropertyName("m")] int M,
    [property: JsonPropertyName("ef_construct")] int EfConstruct);

internal sealed record QdrantExists(
    [property: JsonPropertyName("result")] QdrantExistsResult? Result);

internal sealed record QdrantExistsResult(
    [property: JsonPropertyName("exists")] bool Exists);

internal sealed record QdrantGetCollection(
    [property: JsonPropertyName("result")] QdrantCollectionResult? Result);

internal sealed record QdrantCollectionResult(
    [property: JsonPropertyName("config")] QdrantCollectionConfig? Config);

internal sealed record QdrantCollectionConfig(
    [property: JsonPropertyName("params")] QdrantCollectionParams? Params);

internal sealed record QdrantCollectionParams(
    [property: JsonPropertyName("vectors")] JsonElement? Vectors,
    [property: JsonPropertyName("sparse_vectors")] JsonElement? SparseVectors);

internal sealed record QdrantListCollections(
    [property: JsonPropertyName("result")] QdrantListResult? Result);

internal sealed record QdrantListResult(
    [property: JsonPropertyName("collections")] IReadOnlyList<QdrantCollectionName>? Collections);

internal sealed record QdrantCollectionName(
    [property: JsonPropertyName("name")] string Name);

// The store's view of a point to upsert: a dense vector, an optional sparse (lexical) vector, and
// the payload. The client turns this into the wire shape (bare array vs {dense, sparse}) per the
// collection's named-ness.
internal sealed record QdrantPoint(string Id, float[] Dense, QdrantSparse? Sparse, JsonElement Payload);

// The serialized wire point: `vector` is whatever VectorValue built.
internal sealed record QdrantWirePoint(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] JsonElement Vector,
    [property: JsonPropertyName("payload")] JsonElement Payload);

internal sealed record QdrantUpsertRequest(
    [property: JsonPropertyName("points")] IReadOnlyList<QdrantWirePoint> Points);

internal sealed record QdrantRetrieveRequest(
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("with_vector")] bool WithVector);

internal sealed record QdrantRetrieveResponse(
    [property: JsonPropertyName("result")] IReadOnlyList<QdrantRetrievedPoint>? Result);

// `vector` is a JsonElement because a hybrid-capable collection returns a named map
// ({dense: [...], sparse: {...}}) while a 3.1 collection returns a bare array; the store extracts the
// dense floats from whichever shape it is.
internal sealed record QdrantRetrievedPoint(
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("vector")] JsonElement? Vector);

internal sealed record QdrantDeletePointsRequest(
    [property: JsonPropertyName("points")] IReadOnlyList<string> Points);

internal sealed record QdrantDeleteByFilterRequest(
    [property: JsonPropertyName("filter")] QdrantFilter Filter);

internal sealed record QdrantCountRequest(
    [property: JsonPropertyName("filter")] QdrantFilter? Filter,
    [property: JsonPropertyName("exact")] bool Exact);

internal sealed record QdrantCountResponse(
    [property: JsonPropertyName("result")] QdrantCountResult? Result);

internal sealed record QdrantCountResult(
    [property: JsonPropertyName("count")] long Count);

internal sealed record QdrantSearchRequest(
    [property: JsonPropertyName("vector")] JsonElement Vector,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("filter")] QdrantFilter? Filter,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("with_vector")] bool WithVector,
    [property: JsonPropertyName("params")] QdrantSearchParams? Params);

internal sealed record QdrantSearchParams(
    [property: JsonPropertyName("hnsw_ef")] int HnswEf);

internal sealed record QdrantSearchResponse(
    [property: JsonPropertyName("result")] IReadOnlyList<QdrantScoredPoint>? Result);

internal sealed record QdrantScoredPoint(
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

// ---- Query API (hybrid fusion + sparse search, phase 34) -------------------------------
// `query` and each prefetch's `query` are JsonElements so one request type carries a fusion selector
// ({"fusion":"rrf"}), a dense array, or a sparse {indices,values} interchangeably.
internal sealed record QdrantQueryRequest(
    [property: JsonPropertyName("prefetch")] IReadOnlyList<QdrantPrefetch>? Prefetch,
    [property: JsonPropertyName("query")] JsonElement Query,
    [property: JsonPropertyName("using")] string? Using,
    [property: JsonPropertyName("filter")] QdrantFilter? Filter,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("with_vector")] bool WithVector);

internal sealed record QdrantPrefetch(
    [property: JsonPropertyName("query")] JsonElement Query,
    [property: JsonPropertyName("using")] string Using,
    [property: JsonPropertyName("filter")] QdrantFilter? Filter,
    [property: JsonPropertyName("limit")] int Limit);

internal sealed record QdrantFusion(
    [property: JsonPropertyName("fusion")] string Fusion);

internal sealed record QdrantQueryResponse(
    [property: JsonPropertyName("result")] QdrantQueryResult? Result);

internal sealed record QdrantQueryResult(
    [property: JsonPropertyName("points")] IReadOnlyList<QdrantScoredPoint>? Points);

internal sealed record QdrantScrollRequest(
    [property: JsonPropertyName("filter")] QdrantFilter? Filter,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")] JsonElement? Offset,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("with_vector")] bool WithVector);

internal sealed record QdrantScrollResponse(
    [property: JsonPropertyName("result")] QdrantScrollResult? Result);

internal sealed record QdrantScrollResult(
    [property: JsonPropertyName("points")] IReadOnlyList<QdrantRetrievedPoint>? Points,
    [property: JsonPropertyName("next_page_offset")] JsonElement? NextPageOffset);

// A Qdrant filter: exact-match AND across payload keys, which is exactly `FlatIndex`'s metadata
// filter semantics. A point missing the key never matches, so null-metadata is excluded — the same
// rule the local and postgres providers honour.
internal sealed record QdrantFilter(
    [property: JsonPropertyName("must")] IReadOnlyList<QdrantFieldCondition> Must);

internal sealed record QdrantFieldCondition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("match")] QdrantMatch Match);

internal sealed record QdrantMatch(
    [property: JsonPropertyName("value")] string Value);

internal sealed record QdrantErrorEnvelope(
    [property: JsonPropertyName("status")] QdrantErrorStatus? Status);

internal sealed record QdrantErrorStatus(
    [property: JsonPropertyName("error")] string? Error);
