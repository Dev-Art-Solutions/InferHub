using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using InferHub.Coordinator.Services;
using InferHub.Shared.ChatGpt;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.ChatGpt;

/// <summary><c>ChatGpt:</c> — where the coordinator keeps its own ChatGPT sessions (phase 103).</summary>
public sealed class ChatGptOptions
{
    public const string SectionName = "ChatGpt";

    /// <summary>
    /// One <c>host.json</c> (this coordinator's <c>ext_agent_host_id</c>) and one sub-directory per
    /// <c>chatgpt</c> provider. Relative by default; the image points it at <c>/data/chatgpt</c>.
    /// Nothing is created here until a provider of that type is configured and somebody signs in.
    /// </summary>
    public string DataDirectory { get; set; } = "./data/chatgpt";

    /// <summary>How long a pairing code from the console stays valid. One use either way.</summary>
    public int PairingMinutes { get; set; } = 10;

    /// <summary>
    /// After <c>subscription_sharing_usage_limit_exceeded</c>, how long new requests on that account
    /// are refused here with the same 429 rather than sent into it again.
    /// </summary>
    public int UsageLimitPauseSeconds { get; set; } = 300;
}

/// <summary>
/// The coordinator's ChatGPT sessions, one per <c>chatgpt</c> provider, and the one-time pairings
/// the console creates for them (phase 103).
/// </summary>
/// <remarks>
/// <b>The coordinator's own, and nothing else's</b> (103 D1). A node that signs in to ChatGPT does it
/// in its own process, into its own store, under its own host id — nothing on this side has a path
/// that could read it, and nothing on the hub's SignalR surface carries a token.
/// </remarks>
public sealed class ChatGptAccounts(
    IProviderRegistry providers,
    IOptions<ChatGptOptions> options,
    IHostEnvironment environment,
    IHttpClientFactory httpClientFactory,
    TimeProvider time)
{
    public const string HttpClientName = "inferhub-chatgpt-oauth";

    private readonly ConcurrentDictionary<string, ChatGptSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Pairing> pairings = new(StringComparer.Ordinal);
    private IdTokenValidator? validator;

    public TimeSpan UsagePause => TimeSpan.FromSeconds(Math.Max(1, options.Value.UsageLimitPauseSeconds));

    /// <summary>The enabled <c>chatgpt</c> providers, by id.</summary>
    public IReadOnlyList<ProviderRoute> Providers => providers.Configured
        .Where(route => route.Definition.NormalizedType() == ProviderDefinition.TypeChatGpt)
        .ToArray();

    public bool IsChatGptProvider(string id)
        => Providers.Any(route => string.Equals(route.Id, id, StringComparison.OrdinalIgnoreCase));

    public ChatGptSession Session(string providerId)
    {
        if (!IsChatGptProvider(providerId))
        {
            throw new KeyNotFoundException($"no enabled provider '{providerId}' of type {ProviderDefinition.TypeChatGpt} is configured on this hub.");
        }

        return sessions.GetOrAdd(providerId.ToLowerInvariant(), id =>
        {
            var root = Root();
            var store = new ChatGptCredentialStore(Path.Combine(root, id), Path.Combine(root, ChatGptCredentialStore.HostFileName));
            return new ChatGptSession(store, CreateHttpClient, Validator(), time);
        });
    }

    public IdTokenValidator Validator()
        => validator ??= new IdTokenValidator(
            async cancellationToken =>
            {
                using var http = CreateHttpClient();
                return await http.GetStringAsync(ChatGptOAuth.JwksUri, cancellationToken);
            },
            time);

    public HttpClient CreateHttpClient()
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(60);
        return http;
    }

    /// <summary>
    /// A one-time code for the sign-in helper, bound to this provider, the account it reconnects
    /// (or none, for a new registration) and a nonce the coordinator chose. Only its hash is kept.
    /// </summary>
    public (string Code, DateTimeOffset ExpiresAtUtc) CreatePairing(string providerId, string? registrationId, bool newAccount, string by)
    {
        var session = Session(providerId);
        var hints = session.Hints(registrationId, newAccount);
        var code = "ihp_" + ChatGptOAuth.RandomToken(24);
        var expires = time.GetUtcNow() + TimeSpan.FromMinutes(Math.Max(1, options.Value.PairingMinutes));

        Sweep();

        pairings[Hash(code)] = new Pairing(
            providerId.ToLowerInvariant(),
            hints,
            ChatGptOAuth.RandomToken(32),
            expires,
            by);

        return (code, expires);
    }

    /// <summary>The pairing a helper's code names, if it is live. Does not consume it.</summary>
    public Pairing? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        Sweep();
        return pairings.TryGetValue(Hash(code.Trim()), out var pairing) ? pairing : null;
    }

    /// <summary>Consumes the code: whatever happens next, it cannot be used again.</summary>
    public Pairing? Take(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        Sweep();
        return pairings.TryRemove(Hash(code.Trim()), out var pairing) ? pairing : null;
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();

        foreach (var (key, pairing) in pairings)
        {
            if (pairing.ExpiresAtUtc <= now)
            {
                pairings.TryRemove(key, out _);
            }
        }
    }

    private string Root()
    {
        var directory = options.Value.DataDirectory;
        return Path.IsPathRooted(directory) ? directory : Path.GetFullPath(Path.Combine(environment.ContentRootPath, directory));
    }

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public sealed record Pairing(string ProviderId, SignInHints Hints, string Nonce, DateTimeOffset ExpiresAtUtc, string CreatedBy);
}
