using System.Collections.Concurrent;
using System.Text.Json;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector.Qdrant;

/// <summary>
/// Qdrant implementation of <see cref="IVectorStore"/>, spoken over <see cref="QdrantClient"/>'s
/// hand-rolled REST — no client package, no gRPC, so this provider adds no dependency. Score
/// sign-conventions mirror <see cref="FlatIndex"/> exactly (Qdrant reports the same numbers with no
/// sign flip), so every existing client keeps seeing the same relevance.
/// <para>
/// Qdrant accepts only an integer or UUID as a point id, so each record's real id becomes a
/// deterministic UUIDv5 point id (see <see cref="QdrantIdMap"/>) and the real id — with the payload
/// and metadata — rides in the point payload under reserved <c>__*</c> keys. Reads unpack it, so
/// nothing above this class ever sees the UUID.
/// </para>
/// <para>
/// Like Postgres this is an external, durable store: node replication / self-healing are off, so
/// this store publishes the two lifecycle events itself. The constructor opens no connection — DI
/// composition and smoke tests must work against a dead Qdrant.
/// </para>
/// </summary>
internal sealed class QdrantVectorStore : IVectorStore
{
    // Reserved payload keys. A record's own payload and metadata are nested so they can never clash
    // with these, and so a metadata filter maps to `__meta.<key>` without ambiguity.
    private const string IdKey = "__id";
    private const string PayloadKey = "__payload";
    private const string MetaKey = "__meta";
    private const string SeqKey = "__seq";
    private const string TsKey = "__ts";

    private const int FilterOverFetchCap = 1000;

    // Coarse keyword (phase 33) scrolls the collection client-side; bound it so a large corpus can't
    // turn a keyword query into a full-collection scan. Real ranked keyword is phase 34's sparse vectors.
    private const int KeywordScanCap = 5000;

    private readonly QdrantClient _client;
    private readonly QdrantStoreOptions _q;
    private readonly DistanceMetric _defaultDistance;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly VectorEvents? _events;
    private readonly ConcurrentDictionary<string, CollectionMeta> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _ops = new(StringComparer.Ordinal);

    public QdrantVectorStore(
        QdrantClient client,
        IOptions<VectorStoreOptions> options,
        ILogger<QdrantVectorStore> logger,
        VectorEvents? events = null)
    {
        _client = client;
        var opts = options.Value;
        _q = opts.Qdrant;
        if (!DistanceMetricExtensions.TryParse(opts.Distance, out _defaultDistance))
        {
            throw new InvalidOperationException($"invalid VectorStore:Distance '{opts.Distance}'");
        }
        _logger = logger;
        _events = events;
    }

    public async Task<CollectionInfo> CreateCollectionAsync(string name, int dimension, string? distance, CancellationToken cancellationToken = default)
    {
        ValidateCollectionName(name);
        if (dimension < 1) throw new ArgumentOutOfRangeException(nameof(dimension), "dimension must be >= 1");

        var metric = distance is null ? _defaultDistance : ParseOrThrow(distance);
        var qname = QdrantName(name);

        if (await _client.CollectionExistsAsync(qname, cancellationToken))
        {
            throw new InvalidOperationException($"collection '{name}' already exists");
        }

        await _client.CreateCollectionAsync(qname, dimension, metric, _q.HnswM, _q.HnswEfConstruct, cancellationToken);

        var wire = metric.ToWireString();
        _cache[name] = new CollectionMeta(dimension, metric, wire);
        _ops[name] = 0;
        _events?.Publish("vector.collection.created", name, new Dictionary<string, object?>
        {
            ["dimension"] = dimension,
            ["distance"] = wire
        });
        return new CollectionInfo(name, dimension, wire, 0, 0);
    }

    public async Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var qname = QdrantName(name);
        if (!await _client.CollectionExistsAsync(qname, cancellationToken))
        {
            return false;
        }

        await _client.DropCollectionAsync(qname, cancellationToken);
        _cache.TryRemove(name, out _);
        _ops.TryRemove(name, out _);
        _events?.Publish("vector.collection.dropped", name);
        return true;
    }

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var names = await InferHubCollectionNamesAsync(cancellationToken);
        var infos = new List<CollectionInfo>(names.Count);
        foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
        {
            var meta = await RequireMetaAsync(name, cancellationToken);
            var count = await _client.CountAsync(QdrantName(name), null, cancellationToken);
            infos.Add(new CollectionInfo(name, meta.Dimension, meta.DistanceWire, count, OpsOf(name)));
        }
        return infos;
    }

    public async Task<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        var meta = await TryLoadMetaAsync(name, cancellationToken);
        if (meta is null) return null;
        var count = await _client.CountAsync(QdrantName(name), null, cancellationToken);
        return new CollectionInfo(name, meta.Value.Dimension, meta.Value.DistanceWire, count, OpsOf(name));
    }

    public async Task<VectorRecord> UpsertAsync(string collection, VectorUpsert upsert, CancellationToken cancellationToken = default)
    {
        var meta = await RequireMetaAsync(collection, cancellationToken);
        if (string.IsNullOrWhiteSpace(upsert.Id)) throw new ArgumentException("id is required", nameof(upsert));
        if (upsert.Vector is null || upsert.Vector.Length == 0) throw new ArgumentException("vector is required", nameof(upsert));
        if (upsert.Vector.Length != meta.Dimension)
        {
            throw new ArgumentException($"vector length {upsert.Vector.Length} does not match collection dimension {meta.Dimension}", nameof(upsert));
        }

        var seq = _ops.AddOrUpdate(collection, 1, (_, v) => v + 1);
        var ts = DateTimeOffset.UtcNow;
        var pointId = QdrantIdMap.ToPointId(upsert.Id);
        var payload = BuildPayload(upsert.Id, upsert.Payload, upsert.Metadata, seq, ts);

        var point = new QdrantPoint(pointId, (float[])upsert.Vector.Clone(), payload);
        await _client.UpsertPointsAsync(QdrantName(collection), [point], cancellationToken);

        return new VectorRecord(upsert.Id, (float[])upsert.Vector.Clone(), upsert.Payload, upsert.Metadata, seq, ts);
    }

    public async Task<VectorRecord?> GetAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        var point = await _client.RetrievePointAsync(QdrantName(collection), QdrantIdMap.ToPointId(id), withVector: true, cancellationToken);
        if (point?.Payload is not { } stored) return null;

        var (realId, payload, metadata, seq, ts) = ParseStored(stored);
        return new VectorRecord(realId, point.Vector ?? [], payload, metadata, seq, ts);
    }

    public async Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        var pointId = QdrantIdMap.ToPointId(id);

        // Qdrant's delete-by-id does not report whether anything matched, so establish existence
        // first to keep the "false when absent" contract the other providers honour.
        var existing = await _client.RetrievePointAsync(QdrantName(collection), pointId, withVector: false, cancellationToken);
        if (existing is null) return false;

        await _client.DeletePointsAsync(QdrantName(collection), [pointId], cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<VectorMatch>> QueryAsync(string collection, VectorQuery query, CancellationToken cancellationToken = default)
    {
        var meta = await RequireMetaAsync(collection, cancellationToken);
        if (query.Vector is null || query.Vector.Length == 0) throw new ArgumentException("query vector is required", nameof(query));
        if (query.Vector.Length != meta.Dimension)
        {
            throw new ArgumentException($"query vector length {query.Vector.Length} does not match collection dimension {meta.Dimension}", nameof(query));
        }

        var k = query.K;
        if (k < 1) return Array.Empty<VectorMatch>();

        var hasFilter = query.Filter is { Count: > 0 };
        // A filtered ANN scan can return fewer than k rows; over-fetch then trim, exactly as the
        // postgres provider does.
        var limit = hasFilter ? Math.Min(k * _q.OverFetchMultiplier, FilterOverFetchCap) : k;

        var scored = await _client.SearchAsync(
            QdrantName(collection), query.Vector, limit, BuildFilter(query.Filter), _q.EfSearch, cancellationToken);

        var matches = new List<VectorMatch>(Math.Min(scored.Count, k));
        foreach (var point in scored)
        {
            if (point.Payload is not { } stored) continue;
            var (realId, payload, metadata, _, _) = ParseStored(stored);
            matches.Add(new VectorMatch(realId, point.Score, payload, metadata));
            if (matches.Count == k) break;
        }
        return matches;
    }

    /// <summary>
    /// Coarse keyword search (phase 33). Qdrant's full-text index is a filter, not a ranking, so this
    /// scrolls a bounded slice of the collection and ranks by term-overlap in the chunk text —
    /// enough to give hybrid a real second branch, but explicitly not BM25. Phase 34 replaces this
    /// with server-side sparse-vector fusion. Records without a text payload contribute nothing,
    /// which is honest (the same stance <see cref="ChunkText"/> takes).
    /// </summary>
    public async Task<IReadOnlyList<VectorMatch>> SearchKeywordAsync(string collection, string query, int k, CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        if (k < 1) return Array.Empty<VectorMatch>();

        var terms = Tokenize(query);
        if (terms.Count == 0) return Array.Empty<VectorMatch>();

        var scored = new List<(string Id, double Score, JsonElement? Payload, IReadOnlyDictionary<string, string>? Meta)>();
        JsonElement? offset = null;
        var scanned = 0;

        while (scanned < KeywordScanCap)
        {
            var page = Math.Min(256, KeywordScanCap - scanned);
            var (points, next) = await _client.ScrollAsync(QdrantName(collection), filter: null, page, offset, withVector: false, cancellationToken);
            if (points.Count == 0) break;
            scanned += points.Count;

            foreach (var point in points)
            {
                if (point.Payload is not { } stored) continue;
                var (realId, payload, metadata, _, _) = ParseStored(stored);
                var text = ChunkText.Extract(payload).ToLowerInvariant();
                if (text.Length == 0) continue;

                double hits = terms.Sum(t => CountOccurrences(text, t));
                if (hits > 0)
                {
                    scored.Add((realId, hits, payload, metadata));
                }
            }

            if (next is null) break;
            offset = next;
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(k)
            .Select(s => new VectorMatch(s.Id, s.Score, s.Payload, s.Meta))
            .ToArray();
    }

    public async Task<IReadOnlyList<VectorEntry>> ScanAsync(
        string collection,
        IReadOnlyDictionary<string, string>? filter,
        int limit,
        string? afterId = null,
        CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        if (limit < 1) return Array.Empty<VectorEntry>();

        // Qdrant scrolls by its own point id (a UUID), which is not the InferHub id order the
        // contract promises. So the matching set is materialised and sorted by real id here, then
        // windowed by afterId + limit — correct, at the cost of reading the whole filtered set per
        // call. For the per-document scans that dominate ingestion this set is small.
        var all = await ScrollAllAsync(collection, BuildFilter(filter), withVector: false, cancellationToken);

        return all
            .Select(ParseStored)
            .Where(e => afterId is null || string.CompareOrdinal(e.Id, afterId) > 0)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(e => new VectorEntry(e.Id, e.Payload, e.Meta, e.Seq, e.Ts))
            .ToArray();
    }

    public async Task<int> DeleteByFilterAsync(
        string collection,
        IReadOnlyDictionary<string, string> filter,
        CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        if (filter.Count == 0) throw new ArgumentException("filter must not be empty", nameof(filter));

        var qname = QdrantName(collection);
        var qfilter = BuildFilter(filter)!;

        // Delete-by-filter returns no count, and the contract must — so count first, then delete.
        var count = await _client.CountAsync(qname, qfilter, cancellationToken);
        if (count == 0) return 0;

        await _client.DeleteByFilterAsync(qname, qfilter, cancellationToken);
        return (int)count;
    }

    /// <summary>Warm the metadata cache from Qdrant at startup; returns the InferHub collection count.</summary>
    internal async Task<int> LoadRegistryCacheAsync(CancellationToken cancellationToken)
    {
        var names = await InferHubCollectionNamesAsync(cancellationToken);
        _cache.Clear();
        foreach (var name in names)
        {
            await TryLoadMetaAsync(name, cancellationToken);
        }
        return _cache.Count;
    }

    private async Task<IReadOnlyList<QdrantRetrievedPoint>> ScrollAllAsync(
        string collection, QdrantFilter? filter, bool withVector, CancellationToken cancellationToken)
    {
        var qname = QdrantName(collection);
        var all = new List<QdrantRetrievedPoint>();
        JsonElement? offset = null;
        while (true)
        {
            var (points, next) = await _client.ScrollAsync(qname, filter, limit: 256, offset, withVector, cancellationToken);
            all.AddRange(points);
            if (next is null || points.Count == 0) break;
            offset = next;
        }
        return all;
    }

    private async Task<IReadOnlyList<string>> InferHubCollectionNamesAsync(CancellationToken cancellationToken)
    {
        var names = await _client.ListCollectionNamesAsync(cancellationToken);
        return names
            .Where(n => n.StartsWith(_q.CollectionPrefix, StringComparison.Ordinal))
            .Select(n => n[_q.CollectionPrefix.Length..])
            .ToArray();
    }

    private async Task<CollectionMeta> RequireMetaAsync(string collection, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(collection, out var cached)) return cached;
        var meta = await TryLoadMetaAsync(collection, cancellationToken);
        if (meta is null) throw new KeyNotFoundException($"collection '{collection}' does not exist");
        return meta.Value;
    }

    private async Task<CollectionMeta?> TryLoadMetaAsync(string collection, CancellationToken cancellationToken)
    {
        var info = await _client.GetCollectionAsync(QdrantName(collection), cancellationToken);
        if (info is null) return null;

        var metric = FromQdrantDistance(info.Value.Distance);
        var meta = new CollectionMeta(info.Value.Dimension, metric, metric.ToWireString());
        _cache[collection] = meta;
        return meta;
    }

    private long OpsOf(string collection) => _ops.TryGetValue(collection, out var v) ? v : 0;

    private string QdrantName(string collection) => _q.CollectionPrefix + collection;

    private DistanceMetric ParseOrThrow(string distance)
    {
        if (!DistanceMetricExtensions.TryParse(distance, out var metric))
        {
            throw new ArgumentException($"unknown distance '{distance}'; expected cosine, dot, or l2", nameof(distance));
        }
        return metric;
    }

    private static DistanceMetric FromQdrantDistance(string qdrant) => qdrant switch
    {
        "Dot" => DistanceMetric.Dot,
        "Euclid" => DistanceMetric.L2,
        _ => DistanceMetric.Cosine
    };

    private static QdrantFilter? BuildFilter(IReadOnlyDictionary<string, string>? filter)
    {
        if (filter is not { Count: > 0 }) return null;
        var must = filter
            .Select(kv => new QdrantFieldCondition($"{MetaKey}.{kv.Key}", new QdrantMatch(kv.Value)))
            .ToArray();
        return new QdrantFilter(must);
    }

    private static JsonElement BuildPayload(string realId, JsonElement? payload, IReadOnlyDictionary<string, string>? metadata, long seq, DateTimeOffset ts)
    {
        var obj = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [IdKey] = realId,
            [SeqKey] = seq,
            [TsKey] = ts.ToString("O")
        };
        if (payload is { } p) obj[PayloadKey] = p;
        if (metadata is { Count: > 0 }) obj[MetaKey] = metadata;
        return JsonSerializer.SerializeToElement(obj);
    }

    private static (string Id, JsonElement? Payload, IReadOnlyDictionary<string, string>? Meta, long Seq, DateTimeOffset Ts) ParseStored(QdrantRetrievedPoint point)
        => ParseStored(point.Payload ?? throw new InvalidOperationException("Qdrant point has no payload"));

    private static (string Id, JsonElement? Payload, IReadOnlyDictionary<string, string>? Meta, long Seq, DateTimeOffset Ts) ParseStored(JsonElement stored)
    {
        var id = stored.TryGetProperty(IdKey, out var idEl) ? idEl.GetString() ?? "" : "";

        JsonElement? payload = stored.TryGetProperty(PayloadKey, out var p) ? p.Clone() : null;

        IReadOnlyDictionary<string, string>? meta = null;
        if (stored.TryGetProperty(MetaKey, out var m) && m.ValueKind == JsonValueKind.Object)
        {
            meta = m.Deserialize<Dictionary<string, string>>();
        }

        var seq = stored.TryGetProperty(SeqKey, out var s) && s.TryGetInt64(out var sv) ? sv : 0;

        var ts = stored.TryGetProperty(TsKey, out var t)
            && t.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(t.GetString(), out var tv)
            ? tv
            : default;

        return (id, payload, meta, seq, ts);
    }

    private static void ValidateCollectionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("collection name is required", nameof(name));
        }
        if (name.IndexOfAny(InvalidCollectionChars) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains('.'))
        {
            throw new ArgumentException($"collection name '{name}' contains invalid characters", nameof(name));
        }
    }

    private static readonly char[] InvalidCollectionChars = [' ', '\t', '\n', '\r', '"', '\'', ':', ';', ',', '{', '}', '(', ')', '[', ']', '<', '>', '|', '?', '*'];

    private static List<string> Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static readonly char[] TokenSeparators =
        " \t\n\r.,;:!?\"'`()[]{}<>/\\|@#$%^&*-_=+~".ToCharArray();

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private readonly record struct CollectionMeta(int Dimension, DistanceMetric Metric, string DistanceWire);
}
