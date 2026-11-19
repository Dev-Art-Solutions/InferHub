using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Users;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Mcp;

/// <summary>A client that registered itself (RFC 7591). A secret, when it asked for one, is kept as SHA-256 only.</summary>
public sealed class OAuthClient
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public string AuthMethod { get; set; } = "none";
    public string? SecretHash { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

/// <summary>A refresh token's grant: who signed in, with which key, to which client. Kept by the token's hash.</summary>
public sealed class OAuthGrant
{
    public string Hash { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string Resource { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
}

public sealed record OAuthTokens(string AccessToken, int ExpiresIn, string RefreshToken, string? Scope);

public sealed record OAuthError(string Error, string Description);

/// <summary>
/// Phase 154's authorization server state. Codes and access tokens live in memory (a restart costs one refresh); clients and
/// refresh grants are saved beside <c>users.json</c> when users are, or a restart would sign every connector out (154 D5).
/// </summary>
public sealed class OAuthStore
{
    public const string Scope = "mcp";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private sealed record Code(string ClientId, string RedirectUri, string Challenge, string UserId, string KeyId, string Resource, string? Scope, DateTimeOffset ExpiresUtc);

    private sealed record Access(string ClientId, string UserId, string KeyId, string Resource, DateTimeOffset ExpiresUtc);

    private sealed class Saved
    {
        public List<OAuthClient> Clients { get; set; } = [];
        public List<OAuthGrant> Grants { get; set; } = [];
    }

    private readonly ConcurrentDictionary<string, OAuthClient> clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthGrant> grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Code> codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Access> access = new(StringComparer.Ordinal);
    private readonly object writeLock = new();
    private readonly string? file;
    private readonly IOptionsMonitor<McpOptions> options;
    private readonly TimeProvider time;

    public OAuthStore(IOptionsMonitor<McpOptions> options, IOptions<UserOptions> users, TimeProvider? time = null)
    {
        this.options = options;
        this.time = time ?? TimeProvider.System;

        if (!users.Value.Enabled || !users.Value.IsFile)
        {
            return;
        }

        var directory = string.IsNullOrWhiteSpace(users.Value.DataDirectory) ? "./data/users" : users.Value.DataDirectory;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "oauth.json");

        if (File.Exists(file))
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), JsonOptions) ?? new Saved();
            foreach (var client in saved.Clients)
            {
                clients[client.Id] = client;
            }

            var now = this.time.GetUtcNow();
            foreach (var grant in saved.Grants.Where(g => g.ExpiresUtc > now))
            {
                grants[grant.Hash] = grant;
            }
        }
    }

    private McpOAuthOptions Settings => options.CurrentValue.OAuth;

    public OAuthClient? GetClient(string? id) => id is null ? null : clients.GetValueOrDefault(id);

    public (OAuthClient? Client, string? Secret, OAuthError? Error) Register(IReadOnlyList<string>? redirectUris, string? name, string? authMethod)
    {
        if (redirectUris is not { Count: > 0 } || redirectUris.Count > 10)
        {
            return (null, null, new OAuthError("invalid_redirect_uri", "give one to ten redirect_uris"));
        }

        foreach (var uri in redirectUris)
        {
            if (!IsAcceptableRedirect(uri))
            {
                return (null, null, new OAuthError("invalid_redirect_uri", $"'{uri}' — use https, http on a loopback address, or an app's own scheme"));
            }
        }

        if (clients.Count >= Math.Max(1, Settings.MaxClients))
        {
            return (null, null, new OAuthError("invalid_client_metadata", "this hub has registered as many clients as it keeps"));
        }

        var method = authMethod?.Trim() switch
        {
            null or "" or "none" => "none",
            "client_secret_post" => "client_secret_post",
            "client_secret_basic" => "client_secret_basic",
            var other => null
        };

        if (method is null)
        {
            return (null, null, new OAuthError("invalid_client_metadata", $"token_endpoint_auth_method '{authMethod}' — use none, client_secret_post or client_secret_basic"));
        }

        string? secret = method == "none" ? null : "ihs_" + UserDirectory.Base64Url(RandomNumberGenerator.GetBytes(32));
        var client = new OAuthClient
        {
            Id = "ihc_" + UserDirectory.Base64Url(RandomNumberGenerator.GetBytes(16)),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 100)],
            RedirectUris = redirectUris.ToList(),
            AuthMethod = method,
            SecretHash = secret is null ? null : UserDirectory.Hash(secret),
            CreatedUtc = time.GetUtcNow()
        };

        lock (writeLock)
        {
            clients[client.Id] = client;
            Save();
        }

        return (client, secret, null);
    }

    /// <summary>Whether a client presented the secret it was given, if it was given one.</summary>
    public static bool SecretMatches(OAuthClient client, string? presented)
    {
        if (client.SecretHash is null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(UserDirectory.Hash(presented)),
            Encoding.ASCII.GetBytes(client.SecretHash));
    }

    public string IssueCode(string clientId, string redirectUri, string challenge, AuthenticatedUser user, string resource, string? scope)
    {
        Sweep();
        var code = "iho_" + UserDirectory.Base64Url(RandomNumberGenerator.GetBytes(32));
        codes[UserDirectory.Hash(code)] = new Code(clientId, redirectUri, challenge, user.Id, user.KeyId, resource, scope, time.GetUtcNow().AddMinutes(5));
        return code;
    }

    public (OAuthTokens? Tokens, OAuthError? Error) Redeem(string? code, string clientId, string? redirectUri, string? verifier, string? resource)
    {
        // Single use: taken out before it is checked, so a code replayed while the first redemption runs finds nothing.
        if (string.IsNullOrEmpty(code) || !codes.TryRemove(UserDirectory.Hash(code), out var stored) || stored.ExpiresUtc <= time.GetUtcNow())
        {
            return (null, new OAuthError("invalid_grant", "the code is unknown, used or expired"));
        }

        if (!string.Equals(stored.ClientId, clientId, StringComparison.Ordinal)
            || !string.Equals(stored.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            return (null, new OAuthError("invalid_grant", "the code was issued to another client or redirect_uri"));
        }

        if (string.IsNullOrEmpty(verifier) || !string.Equals(Challenge(verifier), stored.Challenge, StringComparison.Ordinal))
        {
            return (null, new OAuthError("invalid_grant", "code_verifier does not match the code_challenge"));
        }

        if (!string.IsNullOrEmpty(resource) && !SameResource(resource, stored.Resource))
        {
            return (null, new OAuthError("invalid_target", "resource differs from the one authorised"));
        }

        return (Issue(stored.ClientId, stored.UserId, stored.KeyId, stored.Resource, stored.Scope), null);
    }

    public (OAuthTokens? Tokens, OAuthError? Error) Refresh(string? refreshToken, string clientId, string? resource)
    {
        if (string.IsNullOrEmpty(refreshToken) || !grants.TryRemove(UserDirectory.Hash(refreshToken), out var grant))
        {
            return (null, new OAuthError("invalid_grant", "the refresh token is unknown, used or revoked"));
        }

        if (grant.ExpiresUtc <= time.GetUtcNow() || !string.Equals(grant.ClientId, clientId, StringComparison.Ordinal))
        {
            lock (writeLock)
            {
                Save();
            }

            return (null, new OAuthError("invalid_grant", "the refresh token expired or belongs to another client"));
        }

        if (!string.IsNullOrEmpty(resource) && !SameResource(resource, grant.Resource))
        {
            return (null, new OAuthError("invalid_target", "resource differs from the one authorised"));
        }

        // Rotated: the old token is gone (removed above), the new pair is the only one that works.
        return (Issue(grant.ClientId, grant.UserId, grant.KeyId, grant.Resource, grant.Scope), null);
    }

    /// <summary>The user and key an access token was issued for, while it is alive. The caller still asks the directory.</summary>
    public (string UserId, string KeyId, string Resource)? ResolveAccess(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith("iha_", StringComparison.Ordinal)
            || !access.TryGetValue(UserDirectory.Hash(token), out var entry))
        {
            return null;
        }

        if (entry.ExpiresUtc <= time.GetUtcNow())
        {
            access.TryRemove(UserDirectory.Hash(token), out _);
            return null;
        }

        return (entry.UserId, entry.KeyId, entry.Resource);
    }

    /// <summary>RFC 7009: either kind of token; unknown is not an error.</summary>
    public void Revoke(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var hash = UserDirectory.Hash(token);
        access.TryRemove(hash, out _);
        if (grants.TryRemove(hash, out _))
        {
            lock (writeLock)
            {
                Save();
            }
        }
    }

    /// <summary>Signs a user out of every client. Returns how many grants (refresh tokens) ended.</summary>
    public int RevokeUser(string userId)
    {
        foreach (var (hash, _) in access.Where(a => a.Value.UserId == userId).ToArray())
        {
            access.TryRemove(hash, out _);
        }

        var removed = 0;
        foreach (var (hash, _) in grants.Where(g => g.Value.UserId == userId).ToArray())
        {
            if (grants.TryRemove(hash, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            lock (writeLock)
            {
                Save();
            }
        }

        return removed;
    }

    public int SessionsOf(string userId)
    {
        var now = time.GetUtcNow();
        return grants.Values.Count(g => g.UserId == userId && g.ExpiresUtc > now);
    }

    public static string Challenge(string verifier)
        => UserDirectory.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static bool IsAcceptableRedirect(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        return parsed.Scheme switch
        {
            "https" => true,
            "http" => parsed.IsLoopback,
            "javascript" or "data" or "file" or "vbscript" or "about" or "blob" => false,
            // RFC 8252 7.1: an app's private-use scheme, which by convention has a dot (com.example.app:/callback).
            var scheme => scheme.Contains('.') || scheme is "cursor" or "vscode" or "claude"
        };
    }

    private static bool SameResource(string a, string b)
        => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private OAuthTokens Issue(string clientId, string userId, string keyId, string resource, string? scope)
    {
        var now = time.GetUtcNow();
        var minutes = Math.Clamp(Settings.AccessTokenMinutes, 1, 24 * 60);
        var accessToken = "iha_" + UserDirectory.Base64Url(RandomNumberGenerator.GetBytes(32));
        var refreshToken = "ihr_" + UserDirectory.Base64Url(RandomNumberGenerator.GetBytes(32));

        access[UserDirectory.Hash(accessToken)] = new Access(clientId, userId, keyId, resource, now.AddMinutes(minutes));

        lock (writeLock)
        {
            var hash = UserDirectory.Hash(refreshToken);
            grants[hash] = new OAuthGrant
            {
                Hash = hash,
                ClientId = clientId,
                UserId = userId,
                KeyId = keyId,
                Resource = resource,
                Scope = scope,
                ExpiresUtc = now.AddDays(Math.Clamp(Settings.RefreshTokenDays, 1, 365))
            };
            Save();
        }

        Sweep();
        return new OAuthTokens(accessToken, minutes * 60, refreshToken, scope ?? Scope);
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (hash, code) in codes)
        {
            if (code.ExpiresUtc <= now)
            {
                codes.TryRemove(hash, out _);
            }
        }

        foreach (var (hash, entry) in access)
        {
            if (entry.ExpiresUtc <= now)
            {
                access.TryRemove(hash, out _);
            }
        }
    }

    // Callers hold writeLock.
    private void Save()
    {
        if (file is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var saved = new Saved
        {
            Clients = clients.Values.OrderBy(c => c.CreatedUtc).ToList(),
            Grants = grants.Values.Where(g => g.ExpiresUtc > now).ToList()
        };

        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, JsonOptions));
        File.Move(temp, file, overwrite: true);
    }
}
