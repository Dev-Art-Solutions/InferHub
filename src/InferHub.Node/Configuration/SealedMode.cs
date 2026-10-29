using System.Net;
using System.Net.Sockets;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Configuration;

/// <summary>
/// Phase 122. <c>Node:Sealed</c> — what "outside" means, and every key that would reach it (122 D1, D2).
/// </summary>
/// <remarks>
/// <b>Local is an address you can see is not the internet</b>: loopback, a private or link-local range, the
/// carrier-grade range a Tailscale mesh hands out, a single-label name (a container, a LAN host) and the names
/// reserved for a home or a site (<c>.local</c>, <c>.lan</c>, <c>.internal</c>, <c>.home.arpa</c>). A dotted
/// public name is outside even when it happens to resolve to 10.x today: the node cannot know what it resolves
/// to tomorrow, and the setting is a promise about what it will not do.
/// </remarks>
public static class SealedMode
{
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".lan", ".internal", ".home.arpa", ".localdomain"];

    public static bool IsOn(IConfiguration configuration)
        => configuration.GetSection(NodeOptions.SectionName).GetValue<bool>(nameof(NodeOptions.Sealed));

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.Trim().TrimEnd('.').ToLowerInvariant();

        if (name.StartsWith('[') && name.EndsWith(']'))
        {
            name = name[1..^1];
        }

        if (IPAddress.TryParse(name, out var address))
        {
            return IsPrivate(address);
        }

        if (name.Length == 0 || name is "*" or "+")
        {
            return false;
        }

        return name == "localhost" || !name.Contains('.') || LocalSuffixes.Any(name.EndsWith);
    }

    public static bool IsLocal(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && IsLocalHost(uri.Host);

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 — unique local.
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();

        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    /// <summary>
    /// Every door this configuration leaves open, each naming its key and what to do instead — all of them, so a
    /// sealed node is fixed in one edit rather than one restart per key (122 D2).
    /// </summary>
    public static IReadOnlyList<string> Violations(IConfiguration configuration)
    {
        var doors = new List<string>();
        const string why = "Node:Sealed=true";

        // ---- engines --------------------------------------------------------------------------------------------
        var ollama = configuration["Ollama:Endpoint"] ?? new OllamaOptions().Endpoint;
        var backend = configuration.GetSection(BackendOptions.SectionName).Get<BackendOptions>() ?? new BackendOptions();
        var upstream = new UpstreamBackendOptions();
        configuration.GetSection(UpstreamBackendOptions.LegacySectionName).Bind(upstream);
        configuration.GetSection(UpstreamBackendOptions.SectionName).Bind(upstream);

        void Engine(string label, string type, string? baseUrl)
        {
            switch (type)
            {
                case BackendOptions.ChatGpt:
                    doors.Add($"{label} is chatgpt, which is OpenAI's servers. {why} serves only engines on this network; remove it or turn sealing off.");
                    break;
                case BackendOptions.Ollama:
                    if (!IsLocal(baseUrl ?? ollama))
                    {
                        doors.Add($"{label} drives an Ollama at '{baseUrl ?? ollama}', outside this network. {why} drives an Ollama on this machine or this network.");
                    }

                    break;
                default:
                    if (baseUrl is null)
                    {
                        // A launched engine (colibri, Strata, a llama.cpp the node starts) is a process on this machine.
                        if (type is BackendOptions.OpenRouter or BackendOptions.Anthropic or BackendOptions.Gemini)
                        {
                            doors.Add($"{label} is {type}, a cloud vendor. {why} serves only engines on this network.");
                        }

                        break;
                    }

                    if (!IsLocal(baseUrl))
                    {
                        doors.Add($"{label} ({type}) is at '{baseUrl}', outside this network. {why} serves only engines on this network — a vLLM or llama.cpp at a local address is fine.");
                    }

                    break;
            }
        }

        if (backend.IsMulti)
        {
            foreach (var (name, engine) in backend.Engines)
            {
                var type = (engine.Type ?? string.Empty).Trim().ToLowerInvariant();
                var launched = (type == BackendOptions.LlamaCpp && engine.Serve.IsEnabled)
                               || (type is BackendOptions.Colibri or BackendOptions.Strata && string.IsNullOrWhiteSpace(engine.BaseUrl));
                Engine($"Backend:Engines:{name}", type, launched ? null : string.IsNullOrWhiteSpace(engine.BaseUrl) ? null : engine.BaseUrl.Trim());
            }
        }
        else
        {
            var type = backend.Normalized();
            Engine("Backend:Type", type, type == BackendOptions.Ollama ? null : upstream.ResolvedBaseUrl(type));
        }

        // ---- the node's own API ---------------------------------------------------------------------------------
        var localApi = configuration.GetSection(LocalApiOptions.SectionName).Get<LocalApiOptions>() ?? new LocalApiOptions();

        if (localApi.Enabled)
        {
            foreach (var address in localApi.SplitUrls())
            {
                if (!LocalApiOptions.TryParse(address, out var uri, out var wildcard) || wildcard || !IsPrivateLiteral(uri!.Host))
                {
                    doors.Add($"LocalApi:Urls listens on '{address}', which is not one address on this network. {why} serves its own API on loopback or one private address (e.g. http://127.0.0.1:5081).");
                }
            }

            var retrieval = configuration.GetSection(LocalRetrievalOptions.SectionName);

            if (retrieval.GetValue<bool>(nameof(LocalRetrievalOptions.Enabled)))
            {
                foreach (var url in new[] { retrieval["Url"], retrieval["Qdrant:Url"] })
                {
                    if (!string.IsNullOrWhiteSpace(url) && !IsLocal(url))
                    {
                        doors.Add($"LocalApi:Retrieval keeps its corpus at '{url}', outside this network. {why} keeps it here.");
                    }
                }

                if (PostgresHost(retrieval["Postgres:ConnectionString"]) is { } host && !IsLocalHost(host))
                {
                    doors.Add($"LocalApi:Retrieval:Postgres is at '{host}', outside this network. {why} keeps the corpus here.");
                }
            }
        }

        // ---- direct downloads -----------------------------------------------------------------------------------
        var coordinator = configuration.GetSection(CoordinatorOptions.SectionName).GetValue(nameof(CoordinatorOptions.Enabled), true);

        if (configuration.GetValue<bool>("HuggingFace:Enabled"))
        {
            var endpoint = configuration["HuggingFace:Endpoint"];
            var mirror = !string.IsNullOrWhiteSpace(endpoint) && IsLocal(endpoint);

            if (!mirror && !coordinator)
            {
                doors.Add($"HuggingFace:Enabled downloads from Hugging Face, and this node has no coordinator to fetch through. {why} downloads through its coordinator's Relay or from a mirror on this network (HuggingFace:Endpoint).");
            }
        }

        if (configuration.GetValue<bool>("Ollama:Supervisor:AutoInstall") && !IsLocal(configuration["Ollama:Supervisor:InstallUrl"]))
        {
            doors.Add($"Ollama:Supervisor:AutoInstall downloads Ollama's installer from ollama.com. {why} installs from a mirror on this network (Ollama:Supervisor:InstallUrl) or not at all.");
        }

        var update = configuration.GetSection("Update");

        if ((update.GetValue<bool>("Check") || update.GetValue<bool>("Auto") || update.GetValue<bool>("AllowFromHub"))
            && !IsLocal(update["Source"]))
        {
            doors.Add($"Update:Check/Auto/AllowFromHub download releases from GitHub. {why} updates from a feed on this network (Update:Source) or by hand.");
        }

        if (configuration.GetValue<bool>("Tools:AllowModelDownload"))
        {
            doors.Add($"Tools:AllowModelDownload lets a tool worker fetch weights from the internet. {why} runs tools on weights already on disk.");
        }

        foreach (var provider in configuration.GetSection("CloudMedia:Providers").GetChildren())
        {
            if (provider.GetValue("Enabled", true) && !IsLocal(provider["BaseUrl"]))
            {
                doors.Add($"CloudMedia:Providers:{provider.Key} sends pictures, voices or video to a cloud vendor. {why} makes them here or not at all.");
            }
        }

        return doors;
    }

    /// <summary>A listen address must be an address — a name binds whatever it resolves to.</summary>
    private static bool IsPrivateLiteral(string host)
    {
        var name = host.Trim('[', ']');
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(name, out var address) && IsPrivate(address));
    }

    internal static string? PostgresHost(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);

            if (pair.Length == 2 && (pair[0].Equals("Host", StringComparison.OrdinalIgnoreCase) || pair[0].Equals("Server", StringComparison.OrdinalIgnoreCase)))
            {
                // Npgsql takes a list for failover; every host on it must be here.
                return pair[1].Split(',').Select(h => h.Split(':')[0].Trim()).FirstOrDefault(h => !IsLocalHost(h)) ?? pair[1].Split(',')[0].Split(':')[0].Trim();
            }
        }

        return "localhost";
    }
}

/// <summary>Refuses to start a sealed node with a door open (122 D2). Reads configuration, not other options (38 D1's cycle).</summary>
public sealed class SealedModeValidator(IConfiguration configuration) : IValidateOptions<NodeOptions>
{
    public ValidateOptionsResult Validate(string? name, NodeOptions options)
    {
        if (!options.Sealed)
        {
            return ValidateOptionsResult.Success;
        }

        var doors = SealedMode.Violations(configuration);
        return doors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(doors);
    }
}

/// <summary>
/// The sealed node's way out: its coordinator, and the relay on it (122 D3). Built from configuration once.
/// </summary>
public sealed class SealedEgress
{
    private readonly IReadOnlyList<Uri> coordinators;

    public SealedEgress(IConfiguration configuration)
    {
        var options = configuration.GetSection(CoordinatorOptions.SectionName).Get<CoordinatorOptions>() ?? new CoordinatorOptions();

        coordinators = options.Enabled
            ? options.ResolvedEndpoints()
                .Select(url => Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri : null)
                .OfType<Uri>()
                .ToArray()
            : [];
        Secret = options.EnrollmentSecret;
    }

    public string? Secret { get; }

    /// <summary>Whether there is a hub to fetch through. A solo sealed node downloads nothing.</summary>
    public bool CanRelay => coordinators.Count > 0;

    /// <summary><c>{hub}/node/relay/{upstream}/</c> on the first coordinator — the one an HA list leads with.</summary>
    public Uri? RelayBase(string upstream)
        => CanRelay ? new Uri(coordinators[0], $"{NodeRelay.PathPrefix.TrimStart('/')}/{upstream}/") : null;

    public bool IsCoordinator(Uri uri)
        => coordinators.Any(hub => string.Equals(hub.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase));

    public static string Refusal(string host)
        => $"Node:Sealed — this node does not reach '{host}' itself; it fetches through its coordinator's relay (Relay:Enabled on the hub) or not at all";
}

/// <summary>
/// On every <see cref="IHttpClientFactory"/> client of a sealed node (122 D4): a request to an address outside this
/// network fails before a socket opens, unless it is to the coordinator — and one to the coordinator's relay carries
/// the node's secret. The keys are refused at boot; this is the floor under a code path nobody thought of.
/// </summary>
public sealed class SealedEgressHandler(SealedEgress egress) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            return base.SendAsync(request, cancellationToken);
        }

        if (!egress.IsCoordinator(uri))
        {
            return SealedMode.IsLocalHost(uri.Host)
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException(SealedEgress.Refusal(uri.Host));
        }

        if (uri.AbsolutePath.Contains(NodeRelay.PathPrefix + "/", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(egress.Secret)
            && !request.Headers.Contains(NodeRelay.SecretHeader))
        {
            request.Headers.TryAddWithoutValidation(NodeRelay.SecretHeader, egress.Secret);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
