using InferHub.Shared.Contracts;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// Every enabled cloud media provider's map, resolved once: (capability, local model) → route.
/// </summary>
/// <remarks>
/// Built by both hosts from their own configuration. It holds no state beyond the config it was
/// built from, so "which vendor sees this picture" is answered by one lookup that a test can read.
/// </remarks>
public sealed class CloudMediaCatalog
{
    private readonly Dictionary<(string Capability, string Model), CloudMediaRoute> routes;

    public CloudMediaCatalog(IReadOnlyDictionary<string, CloudMediaProviderDefinition>? providers)
    {
        routes = new Dictionary<(string, string), CloudMediaRoute>(KeyComparer.Instance);

        foreach (var (id, definition) in providers ?? new Dictionary<string, CloudMediaProviderDefinition>())
        {
            if (definition is not { Enabled: true } || definition.ResolvedBaseUrl() is not { } baseUrl)
            {
                continue;
            }

            foreach (var (capability, map) in definition.Media)
            {
                if (!CloudMediaDialects.IsSupportedCapability(capability))
                {
                    continue;
                }

                foreach (var (local, upstream) in map)
                {
                    if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(upstream))
                    {
                        continue;
                    }

                    // First claimant wins here only because the validator has already refused a
                    // second one (104 D2 / 61 D1); this never decides between two in practice.
                    routes.TryAdd(
                        (capability.ToLowerInvariant(), local.Trim()),
                        new CloudMediaRoute(
                            id,
                            definition.NormalizedType(),
                            baseUrl,
                            definition.ApiKey,
                            capability.ToLowerInvariant(),
                            local.Trim(),
                            upstream.Trim(),
                            definition.TimeoutSeconds));
                }
            }
        }
    }

    public static CloudMediaCatalog Empty { get; } = new(null);

    public bool IsEmpty => routes.Count == 0;

    public IEnumerable<CloudMediaRoute> Routes => routes.Values;

    public bool TryResolve(string? capability, string? model, out CloudMediaRoute route)
    {
        route = null!;

        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        if (routes.TryGetValue((capability, model.Trim()), out var found))
        {
            route = found;
            return true;
        }

        return false;
    }

    /// <summary>What these providers let a host declare, as tool capabilities are declared (104 D8).</summary>
    public IReadOnlyList<NodeCapability> Capabilities() =>
        routes.Values
            .GroupBy(route => route.Capability, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new NodeCapability(
                group.Key,
                group.Select(route => route.LocalModel).OrderBy(name => name, StringComparer.Ordinal).ToArray()))
            .ToArray();

    /// <summary>
    /// The startup checks both hosts share. Each error is one sentence naming the key to change.
    /// </summary>
    /// <param name="section">The configuration path the errors name, e.g. <c>CloudMedia:Providers</c>.</param>
    /// <param name="dialectOf">
    /// The host's own reading of a provider's <c>Type</c>: the hub maps <c>openai-compatible</c> to
    /// <see cref="CloudMediaDialects.OpenAi"/> and refuses media on types that have none (104 D3).
    /// Null means "this type cannot carry media", and the error names the type.
    /// </param>
    public static IReadOnlyList<string> Validate(
        IEnumerable<KeyValuePair<string, CloudMediaProviderDefinition>> providers,
        string section,
        Func<string, string?> dialectOf)
    {
        var errors = new List<string>();
        var claimed = new Dictionary<(string, string), string>(KeyComparer.Instance);

        foreach (var (id, provider) in providers)
        {
            if (provider.Media.Count == 0)
            {
                continue;
            }

            var path = $"{section}:{id}";

            if (dialectOf(provider.Type) is not { } dialect)
            {
                errors.Add($"{path}:Media is set on a provider of type '{provider.Type}', which serves no pictures, voices or transcripts. " +
                           $"Media works on an OpenAI-compatible or a Gemini provider.");
                continue;
            }

            if (!provider.Enabled)
            {
                continue;
            }

            var baseUrl = string.IsNullOrWhiteSpace(provider.BaseUrl) ? CloudMediaDialects.DefaultBaseUrl(dialect) : provider.BaseUrl;

            if (baseUrl is null || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                errors.Add($"{path}:BaseUrl must be an absolute http(s) URL for media.");
            }

            if (provider.TimeoutSeconds <= 0)
            {
                errors.Add($"{path}:TimeoutSeconds must be positive.");
            }

            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                errors.Add($"{path}:ApiKey is required for media (set it through the environment, e.g. {path.Replace(":", "__")}__ApiKey).");
            }

            foreach (var (capability, map) in provider.Media)
            {
                if (!CloudMediaDialects.IsSupportedCapability(capability))
                {
                    errors.Add($"{path}:Media:{capability} is not a capability a cloud provider serves. " +
                               $"Use one of: {string.Join(", ", CloudMediaDialects.Capabilities)}" +
                               (string.Equals(capability, CapabilityKinds.Video, StringComparison.OrdinalIgnoreCase)
                                   ? " (cloud video is not in this release)."
                                   : "."));
                    continue;
                }

                foreach (var (local, upstream) in map ?? new Dictionary<string, string>())
                {
                    if (string.IsNullOrWhiteSpace(local) || string.IsNullOrWhiteSpace(upstream))
                    {
                        errors.Add($"{path}:Media:{capability} has an empty model name or vendor id.");
                        continue;
                    }

                    if (claimed.TryGetValue((capability, local.Trim()), out var other))
                    {
                        errors.Add($"'{local}' for '{capability}' is claimed by both {section}:{other} and {path}. " +
                                   "One model is served by exactly one provider — remove one of the two.");
                        continue;
                    }

                    claimed[(capability, local.Trim())] = id;
                }
            }
        }

        return errors;
    }

    private sealed class KeyComparer : IEqualityComparer<(string Capability, string Model)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Capability, string Model) x, (string Capability, string Model) y) =>
            string.Equals(x.Capability, y.Capability, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Capability, string Model) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Capability),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Model));
    }
}
