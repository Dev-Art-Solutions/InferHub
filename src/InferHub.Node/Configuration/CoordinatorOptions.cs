namespace InferHub.Node.Configuration;

public sealed class CoordinatorOptions
{
    public const string SectionName = "Coordinator";

    /// <summary>
    /// Whether this node joins a mesh at all (phase 37). Default <c>true</c>, so every existing
    /// config behaves exactly as it did.
    /// </summary>
    /// <remarks>
    /// Turned off, the node builds no <c>HubConnection</c>, starts no heartbeat or model-refresh
    /// loop, and — the part that matters on a machine with no network — <em>stops requiring a
    /// coordinator URL at all</em>. A solo node on a train must not need a URL it will never dial.
    /// Off together with <c>LocalApi:Enabled</c> is a startup failure: a node that neither joins a
    /// mesh nor serves anyone is a process burning a GPU box for nothing, and the config is a typo.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    public string Url { get; set; } = "http://localhost:5080/";

    /// <summary>
    /// The HA coordinator list (phase 32). Empty — the default — means "just <see cref="Url"/>",
    /// so a single-coordinator node is configured exactly as it was in v2.13. When set, the node
    /// walks the list on every failed connect: only one hub holds the lease and the others refuse
    /// the handshake as standbys, so rotation is how the node finds whoever is leading now.
    /// </summary>
    public IList<string> Endpoints { get; set; } = new List<string>();

    public string EnrollmentSecret { get; set; } = string.Empty;

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan ModelRefreshInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The coordinators to try, in order. Never empty.</summary>
    public IReadOnlyList<string> ResolvedEndpoints()
    {
        var configured = Endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Select(endpoint => endpoint.Trim())
            .ToArray();

        return configured.Length > 0 ? configured : [Url];
    }

    /// <summary>
    /// True when this node has somewhere to fail over to. It changes reconnect strategy, so it is
    /// asked in one place: with a list the node drives its own rotation, and SignalR's automatic
    /// reconnect would only delay that by retrying a hub that has become a standby.
    /// </summary>
    public bool HasFailoverEndpoints() => ResolvedEndpoints().Count > 1;
}
