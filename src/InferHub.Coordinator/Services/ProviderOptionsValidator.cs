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

            var type = provider.NormalizedType();

            if (type is not (ProviderDefinition.TypeOpenAiCompatible or ProviderDefinition.TypeOpenRouter))
            {
                failures.Add($"Providers:{id}:Type is '{provider.Type}'. The types this release knows "
                             + $"are: {ProviderDefinition.TypeOpenAiCompatible}, "
                             + $"{ProviderDefinition.TypeOpenRouter}.");
            }

            // openrouter supplies its own, so a BaseUrl there is an override rather than a
            // requirement — but a *malformed* one is still refused, in either type.
            if (provider.ResolvedBaseUrl() is not { } baseUrl
                || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
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

                if (type == ProviderDefinition.TypeOpenRouter && !OpenRouterModelId().IsMatch(upstream.Trim()))
                {
                    failures.Add($"Providers:{id}:ModelMap:{model} is '{upstream}', which is not an "
                                 + "OpenRouter model id. Every id there is 'vendor/model', optionally "
                                 + "prefixed with '~' for a floating alias and suffixed with ':free', "
                                 + "':batch' or ':thinking' — for example 'qwen/qwen3-coder'.");
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

    /// <summary>
    /// OpenRouter's id shape (62 D5): <c>vendor/model</c>, optional <c>~</c> for a floating "latest"
    /// alias, optional <c>:variant</c>. Checked against the shape and against no catalogue — a
    /// checked-in list of vendors is 48 D1's "usually right" in its purest form, and validating
    /// against the live <c>/models</c> listing would make booting depend on a vendor being up.
    /// </summary>
    /// <remarks>
    /// Read from that listing on the day this was written: <b>419 of 419 ids carry a slash</b>. The
    /// risk, stated rather than mitigated: the day an unnamespaced id ships there, this refuses a
    /// valid configuration — a one-line fix behind a message that says what it wanted, which is the
    /// trade an unknown <c>Type</c> and a doubly-claimed model already make in this file.
    /// </remarks>
    [GeneratedRegex(@"^~?[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*(:[a-z]+)?$")]
    private static partial Regex OpenRouterModelId();
}
