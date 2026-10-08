using InferHub.Node.Backends.Catalog;
using InferHub.Node.Backends.Colibri;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Strata;

/// <summary>
/// <c>Strata:</c> — a <a href="https://github.com/Niko1221/Strata">Strata</a> install this node serves
/// from (phase 99). Strata runs Qwen3.8-Flash-Next, a 125B mixture of experts, on one gaming GPU plus
/// system RAM; each installed size is a <c>strata-&lt;model&gt;.json</c> config its <c>setup.py</c>
/// wrote, served by its own <c>serve/server.py</c>. Everything here is inert unless <see cref="Root"/> is set.
/// </summary>
public sealed class StrataOptions
{
    public const string SectionName = "Strata";

    /// <summary>
    /// The Strata checkout (where <c>setup.py</c> and <c>serve/server.py</c> are). Set: this node
    /// serves the configs installed there as a catalogue (99 D1). Unset: a <c>strata</c> backend is a
    /// Strata server somebody else runs, at <c>Upstream:BaseUrl</c> (or the engine's <c>BaseUrl</c>).
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The interpreter. Unset: Strata's own <c>.venv</c> under <see cref="Root"/>, which its installer makes.</summary>
    public string? Python { get; set; }

    /// <summary>Where the <c>strata-*.json</c> configs are. Unset: <see cref="Root"/>, where <c>setup.py</c> writes them.</summary>
    public string? ConfigDir { get; set; }

    /// <summary><c>setup.py --data-dir</c> for an install from the hub: where 60–110 GB of model files go. Unset: Strata's default.</summary>
    public string? DataDir { get; set; }

    /// <summary>
    /// The key a config's server requires, when it has one and the config does not say it (99 D3).
    /// Environment or user-secrets only. Unset: the config's own <c>api_key</c>, or none.
    /// </summary>
    public string? ApiKey { get; set; }

    public StrataServeOptions Serve { get; set; } = new();

    public StrataInstallOptions Install { get; set; } = new();

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A catalogue: <see cref="Root"/> is where Strata is, so its configs can be served.</summary>
    public bool IsCatalog => !string.IsNullOrWhiteSpace(Root);

    public string ResolvedConfigDir() => string.IsNullOrWhiteSpace(ConfigDir) ? Root!.Trim() : ConfigDir.Trim();

    /// <summary>Strata's own venv when it exists (its installer makes one), else the interpreter on the PATH.</summary>
    public string ResolvedPython()
    {
        if (!string.IsNullOrWhiteSpace(Python))
        {
            return Python.Trim();
        }

        var venv = OperatingSystem.IsWindows()
            ? Path.Combine(Root!.Trim(), ".venv", "Scripts", "python.exe")
            : Path.Combine(Root!.Trim(), ".venv", "bin", "python");

        return File.Exists(venv) ? venv : OperatingSystem.IsWindows() ? "python" : "python3";
    }
}

/// <summary><c>Strata:Serve:</c> — one <c>serve/server.py</c> per loaded config (99 D1, 97's catalogue keys).</summary>
public sealed class StrataServeOptions : ICatalogServeOptions
{
    /// <summary>
    /// The first loopback port. Not Strata's 8080: that is the node images' local API port (a trap
    /// v3.61's notes recorded for llama.cpp). 8095 is <c>server.py</c>'s own default.
    /// </summary>
    public int Port { get; set; } = 8095;

    /// <summary>
    /// How many configs may be loaded at once. A Strata model holds 25–60 GB of RAM and most of a
    /// GPU, so the default is one, and a second request for another size is a switch.
    /// </summary>
    public int MaxLoaded { get; set; } = 1;

    public bool OnDemand { get; set; }

    public TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a launched model may take to answer. Strata maps 25–60 GB and locks part of it for the
    /// GPU before its port opens — 1–3 minutes by its own README, longer from a cold disk.
    /// </summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(20);

    public List<string> Preload { get; set; } = [];

    /// <summary>More catalogue entries, name → config file, beside the <c>strata-*.json</c> in <see cref="StrataOptions.ConfigDir"/>.</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>server.py --engine</c>: <c>strata</c>, or Strata's own <c>mock</c> — canned answers, no GPU and
    /// no weights — for checking a node's wiring before a 70 GB download.
    /// </summary>
    public string Engine { get; set; } = "strata";

    /// <summary><c>server.py --gpu</c>: one card or several, as nvidia-smi numbers them. Unset: the config's.</summary>
    public string? Gpu { get; set; }

    /// <summary>More <c>server.py</c> flags. The ones the node owns, and the ones that keep prompts, are refused (99 D3).</summary>
    public List<string> Arguments { get; set; } = [];
}

/// <summary><c>Strata:Install:</c> — what an install from the hub passes to <c>setup.py</c> (99 D4).</summary>
public sealed class StrataInstallOptions
{
    /// <summary><c>--context</c> in tokens. Unset: setup's recommendation for this box.</summary>
    public int? Context { get; set; }

    /// <summary><c>--vision</c>: <c>no</c>, <c>yes</c> or <c>cpu</c>. The node routes chat only, so it defaults off.</summary>
    public string Vision { get; set; } = "no";

    /// <summary>More <c>setup.py</c> flags (<c>--kv q4_0</c>, <c>--low-ram on</c>, <c>--gpu 1</c>). The node's own are refused.</summary>
    public List<string> Arguments { get; set; } = [];
}

public sealed class StrataOptionsValidator(IOptions<BackendOptions> backend) : IValidateOptions<StrataOptions>
{
    /// <summary>
    /// <c>server.py</c> flags the node owns (it decides where the server listens and what it serves), or
    /// that keep or print prompts: <c>--api-monitor</c> holds the last hundred prompts and answers for a
    /// page anybody with the key can read, and rule 7 says no prompt is kept on any host.
    /// </summary>
    internal static readonly string[] ServeOwned = ["--host", "--port", "--config", "--engine", "--api-key", "--api-monitor", "--open", "--script", "--lazy", "--idle-unload"];

    /// <summary><c>setup.py</c> flags an install from the hub sets itself, or that would start the model or rewrite another one.</summary>
    internal static readonly string[] InstallOwned = ["--family", "--model", "--setup", "--yes", "--no-start", "--host", "--port", "--api-key", "--data-dir", "--browser", "--no-browser", "--update", "--calibrate", "--check", "--inspect", "--rollback-engine"];

    public ValidateOptionsResult Validate(string? name, StrataOptions options)
    {
        var used = backend.Value.IsMulti
            ? backend.Value.Engines.Values.Any(e => e.NormalizedType() == BackendOptions.Strata)
            : backend.Value.Normalized() == BackendOptions.Strata;

        if (!used || !options.IsCatalog)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        var serve = options.Serve;
        var key = $"{StrataOptions.SectionName}:Serve";

        if (serve.MaxLoaded is < 1 or > ColibriOptions.MaxLoadedCeiling)
        {
            failures.Add($"{key}:{nameof(StrataServeOptions.MaxLoaded)} must be between 1 and {ColibriOptions.MaxLoadedCeiling} (got {serve.MaxLoaded}).");
        }
        else if (serve.Port < 1 || serve.Port + serve.MaxLoaded - 1 > 65535)
        {
            failures.Add($"{key}:{nameof(StrataServeOptions.Port)} {serve.Port} leaves no room for {serve.MaxLoaded} loaded model(s), one port each.");
        }

        if (serve.IdleUnload <= TimeSpan.Zero)
        {
            failures.Add($"{key}:{nameof(StrataServeOptions.IdleUnload)} must be positive (got {serve.IdleUnload}).");
        }

        if (serve.LoadTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{key}:{nameof(StrataServeOptions.LoadTimeout)} must be positive (got {serve.LoadTimeout}).");
        }

        if (options.ProbeTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{StrataOptions.SectionName}:{nameof(StrataOptions.ProbeTimeout)} must be positive (got {options.ProbeTimeout}).");
        }

        if (serve.Engine is not ("strata" or "mock"))
        {
            failures.Add($"{key}:{nameof(StrataServeOptions.Engine)} is server.py's --engine: 'strata' or 'mock' (got '{serve.Engine}').");
        }

        foreach (var pair in serve.Models)
        {
            if (!ColibriOptions.IsModelName(pair.Key))
            {
                failures.Add($"{key}:Models:{pair.Key} is not a model name: letters, digits, '.', '_' and '-', up to 64.");
            }

            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                failures.Add($"{key}:Models:{pair.Key} names no config file.");
            }
        }

        var preload = serve.Preload.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (preload.Length > serve.MaxLoaded)
        {
            failures.Add($"{key}:{nameof(StrataServeOptions.Preload)} names {preload.Length} models and {key}:MaxLoaded is {serve.MaxLoaded}; they cannot all stay loaded.");
        }

        Owned(serve.Arguments, ServeOwned, $"{key}:{nameof(StrataServeOptions.Arguments)}", "the node sets it for each loaded model, or it keeps prompts (rule 7)", failures);
        Owned(options.Install.Arguments, InstallOwned, $"{StrataOptions.SectionName}:Install:{nameof(StrataInstallOptions.Arguments)}", "an install from the hub sets it itself", failures);

        if (options.Install.Vision is not ("no" or "yes" or "cpu" or "gpu" or "none"))
        {
            failures.Add($"{StrataOptions.SectionName}:Install:{nameof(StrataInstallOptions.Vision)} is setup.py's --vision: no, yes or cpu (got '{options.Install.Vision}').");
        }

        if (options.Install.Context is < 1024)
        {
            failures.Add($"{StrataOptions.SectionName}:Install:{nameof(StrataInstallOptions.Context)} is a context length in tokens (got {options.Install.Context}).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Owned(IEnumerable<string> arguments, string[] owned, string key, string why, List<string> failures)
    {
        foreach (var argument in arguments.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            var flag = argument.Trim().Split('=')[0];

            if (owned.Contains(flag, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"{key} has '{flag}': {why}.");
            }
        }
    }
}
