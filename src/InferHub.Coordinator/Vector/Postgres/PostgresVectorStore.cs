using System.Collections.Concurrent;
using System.Text.Json;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PgVector = Pgvector.Vector;

namespace InferHub.Coordinator.Vector.Postgres;

/// <summary>
/// PostgreSQL + pgvector implementation of <see cref="IVectorStore"/>. Table-per-collection
/// (pgvector's <c>vector(N)</c> column is dimension-typed), with a registry table tracking
/// each collection's dimension and distance. Score sign-conventions mirror
/// <see cref="FlatIndex"/> exactly so every existing client keeps seeing the same numbers.
/// <para>
/// The constructor opens no connection — DI composition and smoke tests must work against a
/// dead database. In this provider node replication / self-healing are off (Postgres owns
/// durability), so this store publishes the two lifecycle events itself.
/// </para>
/// </summary>
public sealed class PostgresVectorStore : IVectorStore
{
    private const int IvfflatLists = 100;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresStoreOptions _pg;
    private readonly DistanceMetric _defaultDistance;
    private readonly bool _indexIsHnsw;
    private readonly int _commandTimeout;
    private readonly ILogger<PostgresVectorStore> _logger;
    private readonly VectorEvents? _events;
    private readonly ConcurrentDictionary<string, CollectionMeta> _cache = new(StringComparer.Ordinal);

    public PostgresVectorStore(
        NpgsqlDataSource dataSource,
        IOptions<VectorStoreOptions> options,
        ILogger<PostgresVectorStore> logger,
        VectorEvents? events = null)
    {
        _dataSource = dataSource;
        var opts = options.Value;
        _pg = opts.Postgres;
        if (!DistanceMetricExtensions.TryParse(opts.Distance, out _defaultDistance))
        {
            throw new InvalidOperationException($"invalid VectorStore:Distance '{opts.Distance}'");
        }

        _indexIsHnsw = string.Equals(_pg.Index, "hnsw", StringComparison.OrdinalIgnoreCase);
        _commandTimeout = Math.Max(1, _pg.CommandTimeoutSeconds);
        _logger = logger;
        _events = events;
    }

    public async Task<CollectionInfo> CreateCollectionAsync(string name, int dimension, string? distance, CancellationToken cancellationToken = default)
    {
        PostgresSchema.ValidateCollectionName(name);
        if (dimension < 1) throw new ArgumentOutOfRangeException(nameof(dimension), "dimension must be >= 1");

        var metric = distance is null ? _defaultDistance : ParseOrThrow(distance);
        var wire = metric.ToWireString();

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                await ExecuteAsync(conn, tx,
                    $"INSERT INTO {PostgresSchema.QualifiedRegistryTable(_pg.Schema)} (name, dimension, distance) VALUES (@name, @dim, @dist)",
                    cancellationToken,
                    Param("name", name),
                    Param("dim", dimension),
                    Param("dist", wire));

                await ExecuteAsync(conn, tx, PostgresSchema.CreateTableSql(_pg.Schema, _pg.TablePrefix, name, dimension), cancellationToken);
                await ExecuteAsync(conn, tx, PostgresSchema.CreateSequenceSql(_pg.Schema, _pg.TablePrefix, name), cancellationToken);

                var annSql = PostgresSchema.CreateAnnIndexSql(
                    _pg.Schema, _pg.TablePrefix, name, metric, _pg.Index, dimension,
                    _pg.HnswM, _pg.HnswEfConstruction, IvfflatLists);
                if (annSql is not null)
                {
                    await ExecuteAsync(conn, tx, annSql, cancellationToken);
                }
                else if (!string.Equals(_pg.Index, "none", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Collection '{Collection}' has dimension {Dimension} > {Max}; pgvector cannot build an ANN index, falling back to exact scan.",
                        name, dimension, PostgresSchema.MaxAnnDimension);
                }

                await ExecuteAsync(conn, tx, PostgresSchema.CreateGinIndexSql(_pg.Schema, _pg.TablePrefix, name), cancellationToken);

                await tx.CommitAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await tx.RollbackAsync(cancellationToken);
                throw new InvalidOperationException($"collection '{name}' already exists");
            }
        }

        _cache[name] = new CollectionMeta(dimension, metric, wire);
        _events?.Publish("vector.collection.created", name, new Dictionary<string, object?>
        {
            ["dimension"] = dimension,
            ["distance"] = wire
        });
        return new CollectionInfo(name, dimension, wire, 0, 0);
    }

    public async Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        bool existed;
        await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
        {
            await using (var del = new NpgsqlCommand(
                $"DELETE FROM {PostgresSchema.QualifiedRegistryTable(_pg.Schema)} WHERE name = @name RETURNING name", conn, tx)
                { CommandTimeout = _commandTimeout })
            {
                del.Parameters.Add(Param("name", name));
                var deleted = await del.ExecuteScalarAsync(cancellationToken);
                existed = deleted is not null;
            }

            if (existed)
            {
                await ExecuteAsync(conn, tx,
                    $"DROP TABLE IF EXISTS {PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, name)}", cancellationToken);
                await ExecuteAsync(conn, tx,
                    $"DROP SEQUENCE IF EXISTS {PostgresSchema.QualifiedSequence(_pg.Schema, _pg.TablePrefix, name)}", cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        if (!existed) return false;

        _cache.TryRemove(name, out _);
        _events?.Publish("vector.collection.dropped", name);
        return true;
    }

    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var registry = await ReadRegistryAsync(conn, cancellationToken);

        var infos = new List<CollectionInfo>(registry.Count);
        foreach (var (name, meta) in registry)
        {
            infos.Add(await ReadInfoAsync(conn, name, meta, cancellationToken));
        }
        return infos;
    }

    public async Task<CollectionInfo?> GetCollectionAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var meta = await TryLoadMetaAsync(conn, name, cancellationToken);
        if (meta is null) return null;
        return await ReadInfoAsync(conn, name, meta.Value, cancellationToken);
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

        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, collection);
        var seq = PostgresSchema.QualifiedSequence(_pg.Schema, _pg.TablePrefix, collection);
        var sql =
            $"INSERT INTO {table} (id, embedding, payload, metadata, seq_no) " +
            $"VALUES (@id, @emb, @payload, @meta, nextval('{seq}'::regclass)) " +
            "ON CONFLICT (id) DO UPDATE SET embedding = EXCLUDED.embedding, payload = EXCLUDED.payload, " +
            "metadata = EXCLUDED.metadata, seq_no = EXCLUDED.seq_no, updated_at = now() " +
            "RETURNING seq_no, updated_at";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
            cmd.Parameters.Add(Param("id", upsert.Id));
            cmd.Parameters.Add(new NpgsqlParameter("emb", new PgVector(upsert.Vector)));
            cmd.Parameters.Add(JsonbParam("payload", upsert.Payload?.GetRawText()));
            cmd.Parameters.Add(JsonbParam("meta", SerializeMetadata(upsert.Metadata)));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var seqNo = reader.GetInt64(0);
            var updatedAt = reader.GetFieldValue<DateTimeOffset>(1);

            return new VectorRecord(upsert.Id, (float[])upsert.Vector.Clone(), upsert.Payload, upsert.Metadata, seqNo, updatedAt);
        }
        catch (PostgresException ex)
        {
            throw Translate(ex, collection);
        }
    }

    public async Task<VectorRecord?> GetAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, collection);
        var sql = $"SELECT id, embedding, payload, metadata, seq_no, updated_at FROM {table} WHERE id = @id";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
            cmd.Parameters.Add(Param("id", id));

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return ReadRecord(reader);
        }
        catch (PostgresException ex)
        {
            throw Translate(ex, collection);
        }
    }

    public async Task<bool> DeleteAsync(string collection, string id, CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, collection);
        var sql = $"DELETE FROM {table} WHERE id = @id RETURNING id";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
            cmd.Parameters.Add(Param("id", id));
            var deleted = await cmd.ExecuteScalarAsync(cancellationToken);
            return deleted is not null;
        }
        catch (PostgresException ex)
        {
            throw Translate(ex, collection);
        }
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
        // An HNSW scan with a selective post-filter can return fewer than k rows; over-fetch
        // then trim in C#. Exact scan (Index=none) is unaffected but the extra rows are harmless.
        var limit = hasFilter ? Math.Min(k * 4, 1000) : k;

        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, collection);
        var scoreExpr = PostgresSchema.ScoreExpression(meta.Metric);
        var op = PostgresSchema.DistanceOperator(meta.Metric);
        var sql =
            $"SELECT id, {scoreExpr} AS score, payload, metadata FROM {table} " +
            "WHERE (@filter IS NULL OR metadata @> @filter) " +
            $"ORDER BY embedding {op} @q ASC LIMIT {limit}";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);

            if (_indexIsHnsw)
            {
                // SET LOCAL scopes ef_search to this transaction only.
                await ExecuteAsync(conn, tx, $"SET LOCAL hnsw.ef_search = {Math.Max(1, _pg.EfSearch)}", cancellationToken);
            }

            var matches = new List<VectorMatch>(limit);
            await using (var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = _commandTimeout })
            {
                cmd.Parameters.Add(new NpgsqlParameter("q", new PgVector(query.Vector)));
                cmd.Parameters.Add(JsonbParam("filter", hasFilter ? SerializeMetadata(query.Filter) : null));

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetString(0);
                    var score = reader.GetDouble(1);
                    var payload = ReadPayload(reader, 2);
                    var metadata = ReadMetadata(reader, 3);
                    matches.Add(new VectorMatch(id, score, payload, metadata));
                }
            }

            await tx.CommitAsync(cancellationToken);

            if (matches.Count > k) matches.RemoveRange(k, matches.Count - k);
            return matches;
        }
        catch (PostgresException ex)
        {
            throw Translate(ex, collection);
        }
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

        var hasFilter = filter is { Count: > 0 };
        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, collection);

        // The embedding column is deliberately absent from the projection — a scan of a few
        // thousand chunks would otherwise drag every vector across the wire to be discarded.
        // `metadata @> @filter` rides the GIN index created with the table.
        var sql =
            $"SELECT id, payload, metadata, seq_no, updated_at FROM {table} " +
            "WHERE (@filter IS NULL OR metadata @> @filter) " +
            "AND (@after IS NULL OR id > @after) " +
            $"ORDER BY id LIMIT {limit}";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
            cmd.Parameters.Add(JsonbParam("filter", hasFilter ? SerializeMetadata(filter) : null));
            cmd.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Text) { Value = (object?)afterId ?? DBNull.Value });

            var entries = new List<VectorEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new VectorEntry(
                    reader.GetString(0),
                    ReadPayload(reader, 1),
                    ReadMetadata(reader, 2),
                    reader.GetInt64(3),
                    reader.GetFieldValue<DateTimeOffset>(4)));
            }
            return entries;
        }
        catch (PostgresException ex)
        {
            throw Translate(ex, collection);
        }
    }

    public async Task<int> DeleteByFilterAsync(
        string collection,
        IReadOnlyDictionary<string, string> filter,
        CancellationToken cancellationToken = default)
    {
        await RequireMetaAsync(collection, cancellationToken);
        if (filter.Count == 0) throw new ArgumentException("filter must not be empty", nameof(filter));

        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, collection);
        var sql = $"DELETE FROM {table} WHERE metadata @> @filter";

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
            cmd.Parameters.Add(JsonbParam("filter", SerializeMetadata(filter)));
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex)
        {
            throw Translate(ex, collection);
        }
    }

    /// <summary>
    /// Load the whole registry into the metadata cache. Called by the bootstrapper at
    /// startup after the registry table is ensured. Returns the collection count.
    /// </summary>
    internal async Task<int> LoadRegistryCacheAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var registry = await ReadRegistryAsync(conn, cancellationToken);
        _cache.Clear();
        foreach (var (name, meta) in registry)
        {
            _cache[name] = meta;
        }
        return _cache.Count;
    }

    private async Task<CollectionMeta> RequireMetaAsync(string collection, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(collection, out var cached)) return cached;

        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        var meta = await TryLoadMetaAsync(conn, collection, cancellationToken);
        if (meta is null) throw new KeyNotFoundException($"collection '{collection}' does not exist");
        _cache[collection] = meta.Value;
        return meta.Value;
    }

    private async Task<CollectionMeta?> TryLoadMetaAsync(NpgsqlConnection conn, string name, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT dimension, distance FROM {PostgresSchema.QualifiedRegistryTable(_pg.Schema)} WHERE name = @name", conn)
            { CommandTimeout = _commandTimeout };
        cmd.Parameters.Add(Param("name", name));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var dimension = reader.GetInt32(0);
        var wire = reader.GetString(1);
        DistanceMetricExtensions.TryParse(wire, out var metric);
        return new CollectionMeta(dimension, metric, wire);
    }

    private async Task<List<KeyValuePair<string, CollectionMeta>>> ReadRegistryAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        var result = new List<KeyValuePair<string, CollectionMeta>>();
        await using var cmd = new NpgsqlCommand(
            $"SELECT name, dimension, distance FROM {PostgresSchema.QualifiedRegistryTable(_pg.Schema)} ORDER BY name", conn)
            { CommandTimeout = _commandTimeout };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var dimension = reader.GetInt32(1);
            var wire = reader.GetString(2);
            DistanceMetricExtensions.TryParse(wire, out var metric);
            result.Add(new KeyValuePair<string, CollectionMeta>(name, new CollectionMeta(dimension, metric, wire)));
        }
        return result;
    }

    private async Task<CollectionInfo> ReadInfoAsync(NpgsqlConnection conn, string name, CollectionMeta meta, CancellationToken cancellationToken)
    {
        var table = PostgresSchema.QualifiedTable(_pg.Schema, _pg.TablePrefix, name);
        var seq = PostgresSchema.QualifiedSequence(_pg.Schema, _pg.TablePrefix, name);
        var sql = $"SELECT (SELECT count(*) FROM {table}), COALESCE(pg_sequence_last_value('{seq}'::regclass), 0)";

        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var count = reader.GetInt64(0);
        var ops = reader.GetInt64(1);
        return new CollectionInfo(name, meta.Dimension, meta.DistanceWire, count, ops);
    }

    private static VectorRecord ReadRecord(NpgsqlDataReader reader)
    {
        var id = reader.GetString(0);
        var embedding = reader.GetFieldValue<PgVector>(1).ToArray();
        var payload = ReadPayload(reader, 2);
        var metadata = ReadMetadata(reader, 3);
        var seqNo = reader.GetInt64(4);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(5);
        return new VectorRecord(id, embedding, payload, metadata, seqNo, updatedAt);
    }

    private static JsonElement? ReadPayload(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        using var doc = JsonDocument.Parse(reader.GetString(ordinal));
        return doc.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, string>? ReadMetadata(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(ordinal));
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null ? null : JsonSerializer.Serialize(metadata);

    private static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var p in parameters) cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Param(string name, object value) => new(name, value);

    private static NpgsqlParameter JsonbParam(string name, string? json) =>
        new(name, NpgsqlDbType.Jsonb) { Value = (object?)json ?? DBNull.Value };

    private DistanceMetric ParseOrThrow(string distance)
    {
        if (!DistanceMetricExtensions.TryParse(distance, out var metric))
        {
            throw new ArgumentException($"unknown distance '{distance}'; expected cosine, dot, or l2", nameof(distance));
        }
        return metric;
    }

    // Map Postgres errors to the exception types VectorEndpoints already handles: a table that
    // vanished (collection dropped concurrently) → 404; a unique clash → 409. Anything else
    // is a real fault and rethrows unchanged.
    private static Exception Translate(PostgresException ex, string collection) => ex.SqlState switch
    {
        PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedObject
            => new KeyNotFoundException($"collection '{collection}' does not exist"),
        PostgresErrorCodes.UniqueViolation
            => new InvalidOperationException(ex.MessageText),
        _ => ex
    };

    private readonly record struct CollectionMeta(int Dimension, DistanceMetric Metric, string DistanceWire);
}
