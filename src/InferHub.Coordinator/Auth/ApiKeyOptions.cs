namespace InferHub.Coordinator.Auth;

public sealed class ApiKeyOptions
{
    public const string SectionName = "Auth";

    public List<string> ApiKeys { get; set; } = new();

    /// <summary>
    /// Named clients (phase 25). Entries in the flat <see cref="ApiKeys"/> list keep working
    /// as anonymous unlimited clients — the two lists coexist.
    /// </summary>
    public List<ClientConfig> Clients { get; set; } = new();

    public List<string> AdminApiKeys { get; set; } = new();

    public bool RequireAuthForLoopback { get; set; }

    public string? NodeEnrollmentSecret { get; set; }
}
