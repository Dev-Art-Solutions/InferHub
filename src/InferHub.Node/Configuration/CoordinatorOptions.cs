namespace InferHub.Node.Configuration;

public sealed class CoordinatorOptions
{
    public const string SectionName = "Coordinator";

    public string Url { get; set; } = "http://localhost:5080/";

    public string EnrollmentSecret { get; set; } = string.Empty;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ModelRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}
