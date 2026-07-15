using System.Text.RegularExpressions;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Coordinator.Vector.Postgres;

/// <summary>
/// Pure, side-effect-free SQL/identifier helpers for the Postgres vector provider. No
/// <c>NpgsqlConnection</c> lives here on purpose — everything in this file is unit-testable
/// without a database. Collection names are validated stricter than <see cref="LocalVectorStore"/>
/// (<c>^[a-z0-9_]{1,48}$</c>) because they become SQL identifiers, and are still quoted on the
/// way into every statement.
/// </summary>
internal static partial class PostgresSchema
{
    /// <summary>pgvector's HNSW / IVFFlat indexes only support up to this many dimensions.</summary>
    public const int MaxAnnDimension = 2000;

    [GeneratedRegex("^[a-z0-9_]{1,48}$")]
    private static partial Regex CollectionNameRegex();

    public static void ValidateCollectionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("collection name is required", nameof(name));
        }

        if (!CollectionNameRegex().IsMatch(name))
        {
            throw new ArgumentException($"collection name '{name}' contains invalid characters", nameof(name));
        }
    }

    /// <summary>Quote a SQL identifier, doubling any embedded double-quote.</summary>
    public static string QuoteIdent(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public static string TableName(string prefix, string collection) => prefix + collection;

    public static string SequenceName(string prefix, string collection) => prefix + collection + "_seq";

    public static string QualifiedTable(string schema, string prefix, string collection) =>
        QuoteIdent(schema) + "." + QuoteIdent(TableName(prefix, collection));

    public static string QualifiedSequence(string schema, string prefix, string collection) =>
        QuoteIdent(schema) + "." + QuoteIdent(SequenceName(prefix, collection));

    public static string QualifiedRegistryTable(string schema) =>
        QuoteIdent(schema) + "." + QuoteIdent("collections");

    /// <summary>pgvector operator-class for the given distance metric (shared by hnsw and ivfflat).</summary>
    public static string OpClass(DistanceMetric metric) => metric switch
    {
        DistanceMetric.Cosine => "vector_cosine_ops",
        DistanceMetric.Dot => "vector_ip_ops",
        DistanceMetric.L2 => "vector_l2_ops",
        _ => "vector_cosine_ops"
    };

    /// <summary>The pgvector distance operator (all "smaller is better", so ORDER BY ... ASC).</summary>
    public static string DistanceOperator(DistanceMetric metric) => metric switch
    {
        DistanceMetric.Cosine => "<=>",
        DistanceMetric.Dot => "<#>",
        DistanceMetric.L2 => "<->",
        _ => "<=>"
    };

    /// <summary>
    /// SQL expression that reproduces <see cref="FlatIndex"/>'s score sign-convention for the
    /// metric, where <paramref name="queryParam"/> is the query-vector placeholder.
    /// </summary>
    public static string ScoreExpression(DistanceMetric metric, string queryParam = "@q") => metric switch
    {
        DistanceMetric.Cosine => $"1 - (embedding <=> {queryParam})",
        DistanceMetric.Dot => $"-(embedding <#> {queryParam})",
        DistanceMetric.L2 => $"embedding <-> {queryParam}",
        _ => $"1 - (embedding <=> {queryParam})"
    };

    /// <summary>ANN index applies only for a real index kind and within the dimension ceiling.</summary>
    public static bool SupportsAnnIndex(string indexKind, int dimension) =>
        !string.Equals(indexKind, "none", StringComparison.OrdinalIgnoreCase)
        && dimension is > 0 and <= MaxAnnDimension;

    /// <summary>Text-search configuration used for the keyword index and its queries. English stemming
    /// helps prose; the exact-identifier case rides on the unstemmed token still being present.</summary>
    public const string TextSearchConfig = "english";

    /// <summary>
    /// A <c>tsvector</c> generated from the chunk text — the same <c>text</c>/<c>content</c> payload
    /// convention <see cref="ChunkText"/> reads on the local side. <c>GENERATED ALWAYS ... STORED</c>
    /// keeps it in lock-step with the row with no application write path, so it cannot drift.
    /// </summary>
    public static string ContentTsvExpression() =>
        $"to_tsvector('{TextSearchConfig}', coalesce(payload->>'text', payload->>'content', ''))";

    public static string CreateTableSql(string schema, string prefix, string collection, int dimension)
    {
        var table = QualifiedTable(schema, prefix, collection);
        return
            $"CREATE TABLE {table} (\n" +
            "    id          text PRIMARY KEY,\n" +
            $"    embedding   vector({dimension}) NOT NULL,\n" +
            "    payload     jsonb NULL,\n" +
            "    metadata    jsonb NULL,\n" +
            "    seq_no      bigint NOT NULL,\n" +
            "    updated_at  timestamptz NOT NULL DEFAULT now(),\n" +
            $"    content_tsv tsvector GENERATED ALWAYS AS ({ContentTsvExpression()}) STORED\n" +
            ");";
    }

    /// <summary>Add the keyword column to a collection that predates hybrid search (v2.6). Idempotent.</summary>
    public static string AddContentTsvColumnSql(string schema, string prefix, string collection)
    {
        var table = QualifiedTable(schema, prefix, collection);
        return $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS content_tsv tsvector " +
               $"GENERATED ALWAYS AS ({ContentTsvExpression()}) STORED;";
    }

    public static string CreateContentTsvIndexSql(string schema, string prefix, string collection)
    {
        var table = QualifiedTable(schema, prefix, collection);
        var indexName = QuoteIdent(TableName(prefix, collection) + "_content_tsv_idx");
        return $"CREATE INDEX IF NOT EXISTS {indexName} ON {table} USING gin (content_tsv);";
    }

    /// <summary>
    /// Keyword ranking with <c>ts_rank_cd</c> over <c>websearch_to_tsquery</c> — the ranked, GIN-backed
    /// full-text search Postgres already ships. The score sits on its own scale (not the vector
    /// distance's), which is why hybrid fuses by rank rather than by number.
    /// </summary>
    public static string KeywordSearchSql(string schema, string prefix, string collection, int limit)
    {
        var table = QualifiedTable(schema, prefix, collection);
        return
            $"SELECT id, ts_rank_cd(content_tsv, q) AS score, payload, metadata " +
            $"FROM {table}, websearch_to_tsquery('{TextSearchConfig}', @query) q " +
            "WHERE content_tsv @@ q " +
            $"ORDER BY score DESC, id LIMIT {limit}";
    }

    public static string CreateSequenceSql(string schema, string prefix, string collection) =>
        $"CREATE SEQUENCE {QualifiedSequence(schema, prefix, collection)};";

    /// <summary>
    /// ANN index DDL for the collection, or <c>null</c> when no ANN index should be built
    /// (index kind <c>none</c>, or dimension above the pgvector ceiling — caller falls back
    /// to exact scan).
    /// </summary>
    public static string? CreateAnnIndexSql(
        string schema,
        string prefix,
        string collection,
        DistanceMetric metric,
        string indexKind,
        int dimension,
        int hnswM,
        int hnswEfConstruction,
        int ivfflatLists)
    {
        if (!SupportsAnnIndex(indexKind, dimension))
        {
            return null;
        }

        var table = QualifiedTable(schema, prefix, collection);
        var indexName = QuoteIdent(TableName(prefix, collection) + "_embedding_idx");
        var opClass = OpClass(metric);

        return indexKind.ToLowerInvariant() switch
        {
            "ivfflat" =>
                $"CREATE INDEX {indexName} ON {table} USING ivfflat (embedding {opClass}) WITH (lists = {ivfflatLists});",
            _ =>
                $"CREATE INDEX {indexName} ON {table} USING hnsw (embedding {opClass}) WITH (m = {hnswM}, ef_construction = {hnswEfConstruction});"
        };
    }

    public static string CreateGinIndexSql(string schema, string prefix, string collection)
    {
        var table = QualifiedTable(schema, prefix, collection);
        var indexName = QuoteIdent(TableName(prefix, collection) + "_metadata_idx");
        return $"CREATE INDEX {indexName} ON {table} USING gin (metadata);";
    }

    public static string DropCollectionSql(string schema, string prefix, string collection) =>
        $"DROP TABLE IF EXISTS {QualifiedTable(schema, prefix, collection)};\n" +
        $"DROP SEQUENCE IF EXISTS {QualifiedSequence(schema, prefix, collection)};";

    public static string CreateRegistryTableSql(string schema) =>
        $"CREATE TABLE IF NOT EXISTS {QualifiedRegistryTable(schema)} (\n" +
        "    name        text PRIMARY KEY,\n" +
        "    dimension   integer NOT NULL,\n" +
        "    distance    text    NOT NULL,\n" +
        "    created_at  timestamptz NOT NULL DEFAULT now()\n" +
        ");";
}
