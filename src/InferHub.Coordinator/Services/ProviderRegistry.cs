using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public interface IProviderRegistry
{
    /// <summary>
    /// The provider that may serve this model, or null — which is the answer for every model on a
    /// hub that configured none, and is what makes the whole feature a single false (22 D5).
    /// </summary>
    ProviderRoute? Resolve(string model);

    /// <summary>
    /// The <c>Providers:</c> entries, for <c>/api/status</c>. <b>The projected legacy provider is
    /// deliberately not here</b> — it is already reported in the <c>fallback</c> block, and
    /// reporting it twice would put a deployment that changed nothing into a payload it did not have
    /// in v3.28.
    /// </summary>
    IReadOnlyList<ProviderRoute> Configured { get; }
}

/// <summary>
/// Resolves a model to the provider that claimed it, over the <c>Providers:</c> map plus the
/// <c>Fallback:</c> section projected onto one more provider (phase 61, D2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The projection is why there is one dispatch path rather than two.</b> The alternative —
/// <c>if (fallback.Enabled) … else providers …</c> — is two behaviours, and the branch nobody is
/// developing is the one that keeps working right up until it silently does not. It is also what
/// lets "a deployment that changes no config behaves identically" be asserted against the new code
/// instead of against a detour around it.
/// </para>
/// <para>
/// Resolution is a plain lookup and never a search for the best price or the nearest region:
/// <see cref="ProviderOptionsValidator"/> has already refused a model claimed twice, so at most one
/// entry can match.
/// </para>
/// </remarks>
public sealed class ProviderRegistry(
    IOptions<ProviderOptions> providers,
    IOptions<FallbackOptions> fallback) : IProviderRegistry
{
    /// <summary>The id of the provider projected from the <c>Fallback:</c> section.</summary>
    public const string LegacyId = "fallback";

    public IReadOnlyList<ProviderRoute> Configured => providers.Value.Entries
        .Where(entry => entry.Value.Enabled)
        .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
        .Select(entry => new ProviderRoute(entry.Key, entry.Value, UpstreamModel: string.Empty, Legacy: false))
        .ToArray();

    public ProviderRoute? Resolve(string model)
    {
        foreach (var (id, definition) in providers.Value.Entries)
        {
            if (definition.Enabled && Maps(definition, model) is { } upstream)
            {
                return new ProviderRoute(id, definition, upstream, Legacy: false);
            }
        }

        var legacy = fallback.Value;

        if (!legacy.Enabled || string.IsNullOrWhiteSpace(legacy.BaseUrl))
        {
            return null;
        }

        var projected = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeOpenAiCompatible,
            BaseUrl = legacy.BaseUrl,
            ApiKey = legacy.ApiKey,
            Trigger = legacy.NormalizedTrigger(),
            TimeoutSeconds = legacy.TimeoutSeconds,
            ModelMap = legacy.ModelMap,
            AllowedModels = legacy.AllowedModels
        };

        return Maps(projected, model) is { } legacyUpstream
            ? new ProviderRoute(LegacyId, projected, legacyUpstream, Legacy: true)
            : null;
    }

    /// <summary>The map is the consent, and the allowlist narrows it — 22 D5, unchanged.</summary>
    private static string? Maps(ProviderDefinition definition, string model)
    {
        if (!definition.ModelMap.TryGetValue(model, out var upstream) || string.IsNullOrWhiteSpace(upstream))
        {
            return null;
        }

        if (definition.AllowedModels.Count > 0
            && !definition.AllowedModels.Any(allowed =>
                string.Equals(allowed?.Trim(), model, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return upstream;
    }
}
