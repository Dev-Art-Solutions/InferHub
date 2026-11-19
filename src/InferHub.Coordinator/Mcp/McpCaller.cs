using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Users;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Mcp;

/// <summary>
/// Who is calling <c>/mcp</c> and what they may do there (154 D4). Every credential the hub already accepts works, each with
/// the permissions it has everywhere else: a user's key or an OAuth token as the user's role, an <c>Auth:Clients</c> or
/// <c>Auth:ApiKeys</c> key as a client that runs models, an admin key as an admin that does not run models (the table in
/// the root CLAUDE.md), and an unauthenticated loopback caller as the hub's own operator when loopback is exempt.
/// </summary>
public sealed record McpCaller(string Who, IReadOnlySet<string> Permissions, ResolvedClient Client, string? UserId = null)
{
    public bool Can(string permission) => Permissions.Contains(permission);

    private static readonly IReadOnlySet<string> ClientPermissions =
        new HashSet<string>(StringComparer.Ordinal) { Users.Permissions.Models, Users.Permissions.Inference };

    private static readonly IReadOnlySet<string> AdminKeyPermissions =
        new HashSet<string>(StringComparer.Ordinal) { Users.Permissions.Models, Users.Permissions.AdminRead, Users.Permissions.AdminWrite, Users.Permissions.Users };

    private static readonly IReadOnlySet<string> LoopbackPermissions =
        new HashSet<string>(Users.Permissions.All, StringComparer.Ordinal);

    public static McpCaller? Resolve(HttpContext context, string resource)
    {
        var services = context.RequestServices;
        var auth = services.GetRequiredService<IOptionsMonitor<ApiKeyOptions>>().CurrentValue;
        var token = BearerToken(context.Request.Headers.Authorization.ToString());

        if (token is null)
        {
            var remote = context.Connection.RemoteIpAddress;
            return remote is not null && System.Net.IPAddress.IsLoopback(remote) && !auth.RequireAuthForLoopback
                ? new McpCaller("loopback", LoopbackPermissions, ResolvedClient.Anonymous)
                : null;
        }

        var users = services.GetService<UserDirectory>();

        // An access token is good only for the resource it was issued for (RFC 8707): a token minted for another hub's
        // /mcp — or for this one under a different public address — is refused rather than accepted by accident.
        if (services.GetService<OAuthStore>()?.ResolveAccess(token) is { } granted)
        {
            return users?.Authenticate(granted.UserId, granted.KeyId) is { } signedIn
                   && string.Equals(granted.Resource.TrimEnd('/'), resource.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? FromUser(signedIn)
                : null;
        }

        if (users?.Resolve(token) is { } user)
        {
            return FromUser(user);
        }

        if (services.GetService<IClientRegistry>()?.Resolve(token) is { } client)
        {
            return new McpCaller(client.Id == ResolvedClient.Anonymous.Id ? "anonymous" : "client:" + client.Id, ClientPermissions, client);
        }

        if (AdminApiKeyMiddleware.IsTokenAccepted(token, auth.AdminApiKeys))
        {
            return new McpCaller(AdminApiKeyMiddleware.Fingerprint(token), AdminKeyPermissions, ResolvedClient.Anonymous);
        }

        return null;
    }

    private static McpCaller FromUser(AuthenticatedUser user) => new(user.Who, user.Permissions, user.Client, user.Id);

    internal static string? BearerToken(string? header)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
