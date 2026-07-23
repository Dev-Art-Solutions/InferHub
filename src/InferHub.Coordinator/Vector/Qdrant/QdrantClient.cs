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

    public async Task CreateCollectionAsync(
        string qdrantName, int dimension, DistanceMetric metric, int hnswM, int hnswEfConstruct, CancellationToken cancellationToken)
    {
        var body = new QdrantCreateCollection(
            new QdrantVectorParams(dimension, ToQdrantDistance(metric)),
            new QdrantHnswConfig(hnswM, hnswEfConstruct));
        await SendAsync(HttpMethod.Put, $"collections/{qdrantName}", body, cancellationToken);
    }

    /// <summary>Collection dimension + distance, or null when the collection does not exist.</summary>
    public async Task<(int Dimension, string Distance)?> GetCollectionAsync(string qdrantName, CancellationToken cancellationToken)
    {
        var envelope = await GetAsync<QdrantGetCollection>($"collections/{qdrantName}", cancellationToken, allow404: true);
        var vectors = envelope?.Result?.Config?.Params?.Vectors;
        if (vectors is null) return null;
        return (vectors.Size, vectors.Distance);
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

    public async Task UpsertPointsAsync(string qdrantName, IReadOnlyList<QdrantPoint> points, CancellationToken cancellationToken)
    {
        var body = new QdrantUpsertRequest(points);
        await SendAsync(HttpMethod.Put, $"collections/{qdrantName}/points?wait=true", body, cancellationToken);
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

    public async Task<IReadOnlyList<QdrantScoredPoint>> SearchAsync(
        string qdrantName, float[] vector, int limit, QdrantFilter? filter, int? efSearch, CancellationToken cancellationToken)
    {
        var body = new QdrantSearchRequest(
            vector, limit, filter, WithPayload: true, WithVector: false,
            efSearch is { } ef ? new QdrantSearchParams(ef) : null);
        var result = await SendAsync<QdrantSearchRequest, QdrantSearchResponse>(
            HttpMethod.Post, $"collections/{qdrantName}/points/search", body, cancellationToken);
        return result?.Result ?? [];
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

internal sealed record QdrantCreateCollection(
    [property: JsonPropertyName("vectors")] QdrantVectorParams Vectors,
    [property: JsonPropertyName("hnsw_config")] QdrantHnswConfig HnswConfig);

internal sealed record QdrantVectorParams(
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("distance")] string Distance);

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
    [property: JsonPropertyName("vectors")] QdrantVectorParams? Vectors);

internal sealed record QdrantListCollections(
    [property: JsonPropertyName("result")] QdrantListResult? Result);

internal sealed record QdrantListResult(
    [property: JsonPropertyName("collections")] IReadOnlyList<QdrantCollectionName>? Collections);

internal sealed record QdrantCollectionName(
    [property: JsonPropertyName("name")] string Name);

internal sealed record QdrantPoint(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("payload")] JsonElement Payload);

internal sealed record QdrantUpsertRequest(
    [property: JsonPropertyName("points")] IReadOnlyList<QdrantPoint> Points);

internal sealed record QdrantRetrieveRequest(
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("with_vector")] bool WithVector);

internal sealed record QdrantRetrieveResponse(
    [property: JsonPropertyName("result")] IReadOnlyList<QdrantRetrievedPoint>? Result);

internal sealed record QdrantRetrievedPoint(
    [property: JsonPropertyName("payload")] JsonElement? Payload,
    [property: JsonPropertyName("vector")] float[]? Vector);

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
    [property: JsonPropertyName("vector")] float[] Vector,
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
