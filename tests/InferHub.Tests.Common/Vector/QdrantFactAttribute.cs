namespace InferHub.Tests.Vector;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) unless <c>INFERHUB_TEST_QDRANT</c> is set to a
/// Qdrant base URL. Keeps the Qdrant integration tests gated without a Testcontainers or
/// SkippableFact dependency (design rule 5) — the skip is decided at discovery time and shows up in
/// the test output, exactly like the Postgres gate.
/// </summary>
public sealed class QdrantFactAttribute : FactAttribute
{
    public QdrantFactAttribute()
    {
        if (QdrantTestGate.Url is null)
        {
            Skip = QdrantTestGate.SkipReason;
        }
    }
}

/// <summary>Theory counterpart of <see cref="QdrantFactAttribute"/>.</summary>
public sealed class QdrantTheoryAttribute : TheoryAttribute
{
    public QdrantTheoryAttribute()
    {
        if (QdrantTestGate.Url is null)
        {
            Skip = QdrantTestGate.SkipReason;
        }
    }
}

internal static class QdrantTestGate
{
    public const string SkipReason = "INFERHUB_TEST_QDRANT not set — see deploy/qdrant/docker-compose.yml";

    public static readonly string? Url = Environment.GetEnvironmentVariable("INFERHUB_TEST_QDRANT");
}
