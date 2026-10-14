using InferHub.Shared.CloudMedia;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Configuration;

/// <summary>
/// Pictures, voices and transcripts from a cloud vendor with this node's own API key (phase 104, D8):
/// <c>CloudMedia:Providers:{id}</c>, each an OpenAI or a Gemini provider with a <c>Media</c> map.
/// </summary>
/// <remarks>
/// Empty by default, and an empty section registers nothing that declares anything — a node that
/// never wrote it is the v3.68 node exactly. The keys are this node's: a coordinator never sends one
/// (103 D1's argument, for API keys).
/// </remarks>
public sealed class CloudMediaOptions
{
    public const string SectionName = "CloudMedia";

    public Dictionary<string, CloudMediaProviderDefinition> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CloudMediaOptionsValidator : IValidateOptions<CloudMediaOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudMediaOptions options)
    {
        var failures = CloudMediaCatalog.Validate(
            options.Providers,
            $"{CloudMediaOptions.SectionName}:Providers",
            type => type?.Trim().ToLowerInvariant() is CloudMediaDialects.OpenAi or CloudMediaDialects.Gemini
                ? type.Trim().ToLowerInvariant()
                : null);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
