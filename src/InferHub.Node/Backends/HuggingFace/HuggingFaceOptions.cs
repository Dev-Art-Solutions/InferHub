using InferHub.Node.Backends.Colibri;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.HuggingFace;

/// <summary>
/// <c>HuggingFace:</c> — the node fetches models from Hugging Face when the hub hands it a link
/// (phase 98). Off unless <see cref="Enabled"/>: a GPU box reaches the internet only when its
/// operator said it may (39 D7).
/// </summary>
public sealed class HuggingFaceOptions
{
    public const string SectionName = "HuggingFace";

    public const string DefaultEndpoint = "https://huggingface.co";

    public bool Enabled { get; set; }

    /// <summary>Where the node downloads from: Hugging Face, or a mirror that speaks its API.</summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>For gated and private repos. Environment or user-secrets only (<c>HuggingFace__Token</c>).</summary>
    public string? Token { get; set; }

    /// <summary>
    /// The llama.cpp engine whose <c>Serve:ModelsDir</c> receives GGUF models. Unset: the only
    /// <c>llamacpp</c> engine that has one.
    /// </summary>
    public string? LlamaCppEngine { get; set; }

    /// <summary>Convert a safetensors checkpoint for colibri with <c>coli convert</c> (98 D3). Needs a colibri catalogue.</summary>
    public bool Convert { get; set; } = true;

    /// <summary>GGUF downloads running at once. Conversions are always one at a time.</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;
}

/// <summary>
/// Where a download lands (98 D1): the engines' own directories, resolved once from configuration —
/// and since phase 99, whether a link to one of Strata's repos has a Strata install to go to.
/// </summary>
public sealed record HuggingFaceTargets(string? GgufEngine, string? GgufDirectory, string? ColibriDirectory, bool Strata = false)
{
    public static HuggingFaceTargets Resolve(HuggingFaceOptions options, BackendOptions backend, ColibriOptions colibri, out string? problem)
        => Resolve(options, backend, colibri, strata: false, out problem);

    public static HuggingFaceTargets Resolve(HuggingFaceOptions options, BackendOptions backend, ColibriOptions colibri, bool strata, out string? problem)
    {
        problem = null;

        var routers = backend.Engines
            .Where(pair => pair.Value.NormalizedType() == BackendOptions.LlamaCpp && !string.IsNullOrWhiteSpace(pair.Value.Serve.ModelsDir))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? engine = null;
        string? ggufDir = null;

        if (!string.IsNullOrWhiteSpace(options.LlamaCppEngine))
        {
            var named = routers.FirstOrDefault(pair => string.Equals(pair.Key, options.LlamaCppEngine.Trim(), StringComparison.OrdinalIgnoreCase));

            if (named.Key is null)
            {
                problem = $"{HuggingFaceOptions.SectionName}:{nameof(HuggingFaceOptions.LlamaCppEngine)} is '{options.LlamaCppEngine}', which is not a llamacpp engine with Serve:ModelsDir under Backend:Engines"
                    + (routers.Length == 0 ? " (there is none)." : $" (those are: {string.Join(", ", routers.Select(r => r.Key))}).");
            }
            else
            {
                engine = named.Key;
                ggufDir = named.Value.Serve.ModelsDir!.Trim();
            }
        }
        else if (routers.Length == 1)
        {
            engine = routers[0].Key;
            ggufDir = routers[0].Value.Serve.ModelsDir!.Trim();
        }
        else if (routers.Length > 1)
        {
            problem = $"{routers.Length} llamacpp engines have a Serve:ModelsDir ({string.Join(", ", routers.Select(r => r.Key))}); name the one that receives GGUF downloads in {HuggingFaceOptions.SectionName}:{nameof(HuggingFaceOptions.LlamaCppEngine)}.";
        }

        var colibriEngine = backend.IsMulti
            ? backend.Engines.Values.Any(e => e.NormalizedType() == BackendOptions.Colibri)
            : backend.Normalized() == BackendOptions.Colibri;

        var colibriDir = options.Convert && colibriEngine && !string.IsNullOrWhiteSpace(colibri.Serve.ModelsDir)
            ? colibri.Serve.ModelsDir!.Trim()
            : null;

        if (problem is null && ggufDir is null && colibriDir is null && !strata)
        {
            problem = $"{HuggingFaceOptions.SectionName}:Enabled is set, and this node has nowhere to put a model: a GGUF goes to a llamacpp engine's Serve:ModelsDir (Backend:Engines), a checkpoint to Colibri:Serve:ModelsDir, a Strata model to the install at Strata:Root.";
        }

        return new HuggingFaceTargets(engine, ggufDir, colibriDir, strata);
    }
}

public sealed class HuggingFaceOptionsValidator(IOptions<BackendOptions> backend, IOptions<ColibriOptions> colibri, IOptions<Strata.StrataOptions>? strata = null)
    : IValidateOptions<HuggingFaceOptions>
{
    public ValidateOptionsResult Validate(string? name, HuggingFaceOptions options)
    {
        var failures = new List<string>();

        // Reserved whether or not the store is on: a command naming it must never reach an engine.
        if (backend.Value.Engines.Keys.Any(key => string.Equals(key, ModelCommand.EngineHuggingFace, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add($"Backend:Engines:{ModelCommand.EngineHuggingFace} — that name is reserved for the node's Hugging Face downloads; call the engine something else.");
        }

        if (options.Enabled)
        {
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
            {
                failures.Add($"{HuggingFaceOptions.SectionName}:{nameof(HuggingFaceOptions.Endpoint)} must be an absolute http(s) URL (got '{options.Endpoint}').");
            }

            if (options.MaxConcurrentDownloads is < 1 or > 16)
            {
                failures.Add($"{HuggingFaceOptions.SectionName}:{nameof(HuggingFaceOptions.MaxConcurrentDownloads)} must be between 1 and 16 (got {options.MaxConcurrentDownloads}).");
            }

            HuggingFaceTargets.Resolve(options, backend.Value, colibri.Value, strata is not null && Strata.StrataComposition.HasCatalog(strata.Value, backend.Value), out var problem);

            if (problem is not null)
            {
                failures.Add(problem);
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
