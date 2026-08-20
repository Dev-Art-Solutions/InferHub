using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Fails the host at startup rather than letting a misconfigured provider be discovered by a prompt
/// arriving somewhere unexpected (phase 61).
/// </summary>
/// <remarks>
/// <b>The duplicate-mapping refusal (61 D1) is the load-bearing check.</b> Two enabled providers
/// claiming one model turns "whose servers see this prompt" into a question about iteration order
/// over a configuration-bound dictionary — that is, about nothing the operator wrote down. Picking
/// the first is what a gateway usually does and it is wrong here for the same reason cloud burst is
/// off by default: the cost of being surprised is somebody else's data, not a failed request.
/// Treating a duplicate as a failover pair is worse still — it is a second disclosure of the same
/// prompt to a second vendor, which is deferred at the track level and must not arrive by typo.
/// </remarks>
public sealed partial class ProviderOptionsValidator(IOptions<FallbackOptions> fallback)
    : IValidateOptions<ProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, ProviderOptions options)
    {
        var failures = new List<string>();

        // Local model → the provider that claimed it. The legacy Fallback: section is seeded first
        // so an upgrade that adds a Providers: entry over an existing map is caught here, on the
        // upgrade, rather than the first time that model is asked for.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var legacy = fallback.Value;

        if (legacy.Enabled && !string.IsNullOrWhiteSpace(legacy.BaseUrl))
        {
            foreach (var model in legacy.ModelMap.Keys)
            {
                claimed[model] = "the Fallback: section";
            }
        }

        foreach (var (id, provider) in options.Entries)
        {
            if (!ProviderId().IsMatch(id))
            {
                failures.Add($"Providers:{id} — a provider id must be lowercase letters, digits and "
                             + "hyphens; it appears in the X-InferHub-Served-By header and in a metric label.");
            }

            if (!provider.Enabled)
            {
                continue;
            }

            if (provider.NormalizedType() != ProviderDefinition.TypeOpenAiCompatible)
            {
                failures.Add($"Providers:{id}:Type is '{provider.Type}'. The types this release knows "
                             + $"are: {ProviderDefinition.TypeOpenAiCompatible}.");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl)
                || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
            {
                failures.Add($"Providers:{id}:BaseUrl is required and must be an absolute URL "
                             + "when the provider is enabled.");
            }

            if (provider.TimeoutSeconds <= 0)
            {
                failures.Add($"Providers:{id}:TimeoutSeconds must be greater than zero.");
            }

            foreach (var (model, upstream) in provider.ModelMap)
            {
                if (string.IsNullOrWhiteSpace(upstream))
                {
                    failures.Add($"Providers:{id}:ModelMap:{model} has no upstream model name. "
                                 + "An empty mapping is not a way to disable one — remove the entry.");
                    continue;
                }

                if (claimed.TryGetValue(model, out var owner))
                {
                    failures.Add($"model '{model}' is mapped by both {owner} and Providers:{id}. "
                                 + "One model may be claimed by exactly one enabled provider: which "
                                 + "upstream receives a prompt is not something this hub will decide "
                                 + "by configuration ordering.");
                    continue;
                }

                claimed[model] = $"Providers:{id}";
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex ProviderId();
}
