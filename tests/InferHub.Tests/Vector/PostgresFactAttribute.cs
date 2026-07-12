namespace InferHub.Tests.Vector;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) unless <c>INFERHUB_TEST_POSTGRES</c> is
/// set to an Npgsql connection string. Keeps the Postgres integration tests gated without a
/// Testcontainers or SkippableFact dependency (design rule 5) — the skip is decided at
/// discovery time and shows up in the test output.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (PostgresTestGate.ConnectionString is null)
        {
            Skip = PostgresTestGate.SkipReason;
        }
    }
}

/// <summary>Theory counterpart of <see cref="PostgresFactAttribute"/>.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (PostgresTestGate.ConnectionString is null)
        {
            Skip = PostgresTestGate.SkipReason;
        }
    }
}

internal static class PostgresTestGate
{
    public const string SkipReason = "INFERHUB_TEST_POSTGRES not set — see deploy/postgres/docker-compose.yml";

    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("INFERHUB_TEST_POSTGRES");
}
