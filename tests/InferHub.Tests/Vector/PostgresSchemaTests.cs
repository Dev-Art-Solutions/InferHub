using InferHub.Coordinator.Vector.Postgres;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Tests.Vector;

/// <summary>
/// Pure unit tests for the Postgres schema helper — no database required. Pins the score
/// expressions and op-classes that keep parity with <c>FlatIndex</c>, identifier quoting, and
/// the dimension-ceiling decision.
/// </summary>
public class PostgresSchemaTests
{
    [Theory]
    [InlineData(DistanceMetric.Cosine, "1 - (embedding <=> @q)")]
    [InlineData(DistanceMetric.Dot, "-(embedding <#> @q)")]
    [InlineData(DistanceMetric.L2, "embedding <-> @q")]
    public void ScoreExpressionMatchesFlatIndexSignConvention(DistanceMetric metric, string expected)
    {
        Assert.Equal(expected, PostgresSchema.ScoreExpression(metric));
    }

    [Fact]
    public void CreateTableIncludesGeneratedKeywordColumn()
    {
        var sql = PostgresSchema.CreateTableSql("inferhub", "vec_", "docs", 3);
        Assert.Contains("content_tsv tsvector GENERATED ALWAYS AS", sql);
        Assert.Contains("to_tsvector('english', coalesce(payload->>'text', payload->>'content', ''))", sql);
        Assert.Contains("STORED", sql);
    }

    [Fact]
    public void KeywordSearchSqlRanksWithTsRankCdOverWebsearchQuery()
    {
        var sql = PostgresSchema.KeywordSearchSql("inferhub", "vec_", "docs", 10);
        Assert.Contains("ts_rank_cd(content_tsv, q)", sql);
        Assert.Contains("websearch_to_tsquery('english', @query)", sql);
        Assert.Contains("content_tsv @@ q", sql);
        Assert.Contains("LIMIT 10", sql);
    }

    [Fact]
    public void KeywordColumnMigrationIsIdempotent()
    {
        Assert.Contains("ADD COLUMN IF NOT EXISTS content_tsv", PostgresSchema.AddContentTsvColumnSql("inferhub", "vec_", "docs"));
        Assert.Contains("CREATE INDEX IF NOT EXISTS", PostgresSchema.CreateContentTsvIndexSql("inferhub", "vec_", "docs"));
    }

    [Theory]
    [InlineData(DistanceMetric.Cosine, "vector_cosine_ops")]
    [InlineData(DistanceMetric.Dot, "vector_ip_ops")]
    [InlineData(DistanceMetric.L2, "vector_l2_ops")]
    public void OpClassMapsPerDistance(DistanceMetric metric, string expected)
    {
        Assert.Equal(expected, PostgresSchema.OpClass(metric));
    }

    [Theory]
    [InlineData(DistanceMetric.Cosine, "<=>")]
    [InlineData(DistanceMetric.Dot, "<#>")]
    [InlineData(DistanceMetric.L2, "<->")]
    public void DistanceOperatorIsSmallerIsBetterForm(DistanceMetric metric, string expected)
    {
        Assert.Equal(expected, PostgresSchema.DistanceOperator(metric));
    }

    [Fact]
    public void QualifiedTableQuotesSchemaAndDerivedName()
    {
        Assert.Equal("\"inferhub\".\"vec_docs\"", PostgresSchema.QualifiedTable("inferhub", "vec_", "docs"));
    }

    [Fact]
    public void QuoteIdentDoublesEmbeddedQuotes()
    {
        Assert.Equal("\"a\"\"b\"", PostgresSchema.QuoteIdent("a\"b"));
    }

    [Theory]
    [InlineData("docs")]
    [InlineData("my_collection_1")]
    public void ValidateCollectionNameAcceptsSafeNames(string name)
    {
        PostgresSchema.ValidateCollectionName(name); // no throw
    }

    [Theory]
    [InlineData("has\"quote")]
    [InlineData("Upper")]
    [InlineData("with-dash")]
    [InlineData("with.dot")]
    [InlineData("")]
    public void ValidateCollectionNameRejectsUnsafeNames(string name)
    {
        Assert.Throws<ArgumentException>(() => PostgresSchema.ValidateCollectionName(name));
    }

    [Fact]
    public void CreateAnnIndexSqlBuildsHnswWithBuildParams()
    {
        var sql = PostgresSchema.CreateAnnIndexSql("inferhub", "vec_", "docs", DistanceMetric.Cosine, "hnsw", 768, 16, 64, 100);
        Assert.NotNull(sql);
        Assert.Contains("USING hnsw (embedding vector_cosine_ops)", sql);
        Assert.Contains("m = 16, ef_construction = 64", sql);
    }

    [Fact]
    public void CreateAnnIndexSqlBuildsIvfflatWithLists()
    {
        var sql = PostgresSchema.CreateAnnIndexSql("inferhub", "vec_", "docs", DistanceMetric.L2, "ivfflat", 768, 16, 64, 100);
        Assert.NotNull(sql);
        Assert.Contains("USING ivfflat (embedding vector_l2_ops)", sql);
        Assert.Contains("lists = 100", sql);
    }

    [Fact]
    public void NoneIndexKindSkipsAnnIndex()
    {
        Assert.False(PostgresSchema.SupportsAnnIndex("none", 768));
        Assert.Null(PostgresSchema.CreateAnnIndexSql("inferhub", "vec_", "docs", DistanceMetric.Cosine, "none", 768, 16, 64, 100));
    }

    [Fact]
    public void DimensionAboveCeilingSkipsAnnIndex()
    {
        Assert.True(PostgresSchema.SupportsAnnIndex("hnsw", PostgresSchema.MaxAnnDimension));
        Assert.False(PostgresSchema.SupportsAnnIndex("hnsw", PostgresSchema.MaxAnnDimension + 1));
        Assert.Null(PostgresSchema.CreateAnnIndexSql("inferhub", "vec_", "docs", DistanceMetric.Cosine, "hnsw", 3072, 16, 64, 100));
    }

    [Fact]
    public void CreateTableSqlEmbedsDimensionInVectorColumn()
    {
        var sql = PostgresSchema.CreateTableSql("inferhub", "vec_", "docs", 384);
        Assert.Contains("\"inferhub\".\"vec_docs\"", sql);
        Assert.Contains("embedding   vector(384) NOT NULL", sql);
    }
}
