using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Auth;

/// <summary>The identity a bearer token resolved to. Flows on <c>HttpContext.Items</c>.</summary>
public sealed record ResolvedClient(string Id, ClientLimits? Limits, IReadOnlyList<string>? Collections = null)
{
    /// <summary>
    /// A flat <c>Auth:ApiKeys</c> entry, a loopback caller, or no auth at all: one shared
    /// anonymous identity with no limits and no collection scope — exactly what every key was
    /// before phase 25, and before phase 31.
    /// </summary>
    public static readonly ResolvedClient Anonymous = new("anonymous", null);
}

public interface IClientRegistry
{
    /// <summary>
    /// Resolves a presented bearer token. Named clients win over the legacy flat list; a flat
    /// key resolves to <see cref="ResolvedClient.Anonymous"/>; an unknown token is null.
    /// </summary>
    ResolvedClient? Resolve(string presentedToken);

    /// <summary>Whether any client or legacy key is configured at all.</summary>
    bool HasAnyKeys { get; }

    IReadOnlyList<ClientConfig> NamedClients { get; }
}

/// <summary>
/// Hashed-key → client lookup. Every comparison is a SHA-256 + FixedTimeEquals, same as the
/// two key-checking middlewares — and like them, it compares against *every* entry rather
/// than returning early, so timing does not reveal which key matched.
/// </summary>
public sealed class ClientRegistry(IOptionsMonitor<ApiKeyOptions> options) : IClientRegistry
{
    public ResolvedClient? Resolve(string presentedToken)
    {
        var settings = options.CurrentValue;

        Span<byte> presentedHash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken), presentedHash);

        Span<byte> keyHash = stackalloc byte[32];
        ResolvedClient? resolved = null;

        foreach (var client in settings.Clients)
        {
            if (string.IsNullOrEmpty(client.Key))
            {
                continue;
            }

            SHA256.HashData(Encoding.UTF8.GetBytes(client.Key), keyHash);

            if (CryptographicOperations.FixedTimeEquals(presentedHash, keyHash))
            {
                resolved = new ResolvedClient(client.Id, client.Limits, client.Collections);
            }
        }

        foreach (var key in settings.ApiKeys)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            SHA256.HashData(Encoding.UTF8.GetBytes(key), keyHash);

            if (CryptographicOperations.FixedTimeEquals(presentedHash, keyHash))
            {
                resolved ??= ResolvedClient.Anonymous;
            }
        }

        return resolved;
    }

    public bool HasAnyKeys
    {
        get
        {
            var settings = options.CurrentValue;
            return settings.ApiKeys.Any(key => !string.IsNullOrEmpty(key))
                || settings.Clients.Any(client => !string.IsNullOrEmpty(client.Key));
        }
    }

    public IReadOnlyList<ClientConfig> NamedClients => options.CurrentValue.Clients;
}

/// <summary>
/// A duplicate key across two client ids would make attribution depend on list order; a client
/// without an id or key is a config typo. Both fail startup loudly rather than resolve quietly.
/// </summary>
public sealed class ApiKeyOptionsValidator : IValidateOptions<ApiKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiKeyOptions options)
    {
        var failures = new List<string>();
        var seenKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var client in options.Clients)
        {
            if (string.IsNullOrWhiteSpace(client.Id))
            {
                failures.Add("Auth:Clients — every client needs a non-empty Id.");
                continue;
            }

            if (string.IsNullOrEmpty(client.Key))
            {
                failures.Add($"Auth:Clients — client '{client.Id}' has no Key. Set it via env or user-secrets.");
                continue;
            }

            if (seenKeys.TryGetValue(client.Key, out var otherId))
            {
                failures.Add($"Auth:Clients — clients '{otherId}' and '{client.Id}' share the same key; attribution would be ambiguous.");
            }
            else
            {
                seenKeys.Add(client.Key, client.Id);
            }
        }

        // A flat key that is also a named client's key would resolve to the named client and
        // silently stop being anonymous — surface it instead.
        foreach (var key in options.ApiKeys)
        {
            if (!string.IsNullOrEmpty(key) && seenKeys.TryGetValue(key, out var clientId))
            {
                failures.Add($"Auth — a key in Auth:ApiKeys duplicates named client '{clientId}'. Remove one.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
