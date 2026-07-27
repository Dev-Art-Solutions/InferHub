namespace InferHub.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) unless <c>INFERHUB_TEST_OLLAMA_SUPERVISOR=1</c>.
/// Same shape as <c>PostgresFactAttribute</c> / <c>QdrantFactAttribute</c>, and for the same
/// reason: no Testcontainers, no SkippableFact, no new dependency (design rule 5).
/// </summary>
/// <remarks>
/// This gate is stricter than the other two because the tests behind it <em>stop and start a real
/// Ollama on the machine running them</em>. Opting in is opting into that.
/// </remarks>
public sealed class OllamaSupervisorFactAttribute : FactAttribute
{
    public OllamaSupervisorFactAttribute()
    {
        if (!OllamaSupervisorTestGate.Enabled)
        {
            Skip = OllamaSupervisorTestGate.SkipReason;
        }
    }
}

internal static class OllamaSupervisorTestGate
{
    public const string SkipReason =
        "INFERHUB_TEST_OLLAMA_SUPERVISOR not set to 1 — this test stops and restarts the real local Ollama";

    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("INFERHUB_TEST_OLLAMA_SUPERVISOR") == "1";

    public static string Endpoint =>
        Environment.GetEnvironmentVariable("INFERHUB_TEST_OLLAMA_ENDPOINT") ?? "http://localhost:11434/";
}
