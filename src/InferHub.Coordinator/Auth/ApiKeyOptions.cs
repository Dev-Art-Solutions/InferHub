namespace InferHub.Coordinator.Auth;

public sealed class ApiKeyOptions
{
    public const string SectionName = "Auth";

    public List<string> ApiKeys { get; set; } = new();

    public bool RequireAuthForLoopback { get; set; }

    public string? NodeEnrollmentSecret { get; set; }
}
