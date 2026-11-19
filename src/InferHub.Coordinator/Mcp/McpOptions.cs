namespace InferHub.Coordinator.Mcp;

/// <summary>
/// <c>Mcp</c> (phase 154): the coordinator as an MCP server at <c>/mcp</c> (Streamable HTTP). Off by default; when off the
/// route, the OAuth endpoints and the discovery documents do not exist.
/// </summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    public bool Enabled { get; set; }

    /// <summary>
    /// The address clients reach this hub at, e.g. <c>https://hub.example.com</c>. Behind TLS termination the request's own
    /// scheme is <c>http</c>, and an OAuth issuer or resource that says http is one claude.ai and ChatGPT refuse — so set it.
    /// Unset: taken from each request.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Browser origins allowed to call <c>/mcp</c> besides this hub's own (the spec's DNS-rebinding defence). A request with no
    /// <c>Origin</c> header — every server-side client — is not affected.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    public McpOAuthOptions OAuth { get; set; } = new();
}

public sealed class McpOAuthOptions
{
    /// <summary>
    /// The sign-in claude.ai and ChatGPT connectors need: discovery, dynamic client registration, PKCE. Takes effect only
    /// with <c>Users:Enabled</c> — the person signing in is a user, proving it with one of their keys.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public int AccessTokenMinutes { get; set; } = 60;

    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Registration is open (anyone may register a client; only a user's key authorises one), so it is bounded.</summary>
    public int MaxClients { get; set; } = 1000;
}
