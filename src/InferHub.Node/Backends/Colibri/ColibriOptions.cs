using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// <c>Colibri:</c> — what a <c>Backend:Type=colibri</c> node knows about the engine behind it
/// (phase 93). Everything here is inert on any other backend type.
/// </summary>
public sealed class ColibriOptions
{
    public const string SectionName = "Colibri";

    /// <summary>colibri's own default port for <c>coli serve</c>.</summary>
    public const int DefaultPort = 8000;

    /// <summary>The engine's own ceiling on <c>--kv-slots</c>.</summary>
    public const int MaxKvSlots = 16;

    /// <summary>
    /// How many independent KV contexts the engine keeps (<c>coli serve --kv-slots</c>). Above 1,
    /// every request carries a <c>cache_slot</c> derived from its conversation's opening (93 D2),
    /// and the node declares this as its <c>MaxConcurrency</c> unless <c>Node:MaxConcurrency</c>
    /// is set (93 D3). <b>It must match the engine's</b>: a launched engine is started with it; an
    /// external one is the operator's to keep in step, and a slot past the engine's count is a 400.
    /// <b>Only the GLM-5.2/5.3 engines take more than one</b> (colibri v1.12.1's family registry);
    /// every other family refuses to start with a larger value, and the node logs that sentence.
    /// </summary>
    public int KvSlots { get; set; } = 1;

    /// <summary>
    /// Probe <c>GET /health</c> and declare the result on the heartbeat (93 D4, 69's mechanism).
    /// Unlike a vendor, colibri has a free liveness endpoint — the gateway answers it without
    /// touching the engine — so this defaults on.
    /// </summary>
    public bool Watch { get; set; } = true;

    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int UnhealthyThreshold { get; set; } = 3;

    public ColibriServeOptions Serve { get; set; } = new();

    /// <summary>How many catalogue models one node may hold loaded (97 D1) — a port each, and a sanity bound.</summary>
    public const int MaxLoadedCeiling = 16;

    /// <summary>A catalogue name: a token, never a path (97 D4, 95 D4's rule).</summary>
    public static bool IsModelName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Length <= 64
           && char.IsAsciiLetterOrDigit(name[0])
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Where <c>coli serve</c> listens when this node launched it.</summary>
    public string LaunchedBaseUrl() => $"http://127.0.0.1:{Serve.Port}/v1";
}

/// <summary>
/// <c>Colibri:Serve:</c> — the node launches <c>coli serve</c> as its own child (93 D5). Off unless
/// <see cref="Model"/> is set: the path is the consent.
/// </summary>
public sealed class ColibriServeOptions
{
    /// <summary>The converted model directory (<c>coli serve --model</c>). Unset: launch nothing.</summary>
    public string? Model { get; set; }

    /// <summary><c>--model-id</c>: the name the hub routes on. Unset: the directory's own name.</summary>
    public string? ModelId { get; set; }

    public int Port { get; set; } = ColibriOptions.DefaultPort;

    /// <summary>The interpreter that runs the launcher. The gateway is stdlib-only Python.</summary>
    public string Python { get; set; } = OperatingSystem.IsWindows() ? "python" : "python3";

    /// <summary>The <c>coli</c> launcher from a colibri release archive or checkout.</summary>
    public string Launcher { get; set; } = "coli";

    /// <summary>
    /// Phase 97: a directory of converted models, one per sub-directory holding a <c>config.json</c>,
    /// each served under its directory's name and launched when a request names it (97 D1).
    /// </summary>
    public string? ModelsDir { get; set; }

    /// <summary>Phase 97: more catalogue entries, name → converted model directory. A name here wins over the same name in <see cref="ModelsDir"/>.</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Phase 97: how many catalogue models may be loaded at once — one <c>coli serve</c> each, on
    /// <see cref="Port"/>, <see cref="Port"/>+1, … A statement about this box's RAM, so a profile
    /// can never pin more than this (97 D4).
    /// </summary>
    public int MaxLoaded { get; set; } = 1;

    /// <summary>Phase 97: stop a loaded, unpinned model once it has been idle for <see cref="IdleUnload"/> (97 D3).</summary>
    public bool OnDemand { get; set; }

    public TimeSpan IdleUnload { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Phase 97: how long a launched model may take to answer before its load counts as failed.</summary>
    public TimeSpan LoadTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Phase 97: catalogue models loaded when the node starts and kept loaded, until a profile says otherwise.</summary>
    public List<string> Preload { get; set; } = [];

    /// <summary>One model behind one <c>coli serve</c>: the v3.58 shape.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Model);

    /// <summary>A catalogue: many models, loaded on demand (97 D1).</summary>
    public bool IsCatalog => !string.IsNullOrWhiteSpace(ModelsDir) || Models.Count > 0;

    public string ResolvedModelId()
        => string.IsNullOrWhiteSpace(ModelId)
            ? new DirectoryInfo(Model!.TrimEnd('/', '\\')).Name
            : ModelId!.Trim();
}

public sealed class ColibriOptionsValidator(IOptions<BackendOptions> backend, IConfiguration configuration)
    : IValidateOptions<ColibriOptions>
{
    public ValidateOptionsResult Validate(string? name, ColibriOptions options)
    {
        // Phase 95: a colibri *engine* reads this section too, so it is checked for one as well.
        var multi = backend.Value.IsMulti;
        var colibriEngine = multi && backend.Value.Engines.Values.Any(e => e.NormalizedType() == BackendOptions.Colibri);

        if (backend.Value.Normalized() != BackendOptions.Colibri && !colibriEngine)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (options.KvSlots is < 1 or > ColibriOptions.MaxKvSlots)
        {
            failures.Add(
                $"{ColibriOptions.SectionName}:{nameof(ColibriOptions.KvSlots)} must be between 1 and {ColibriOptions.MaxKvSlots} (got {options.KvSlots}).");
        }

        if (options.Watch)
        {
            if (options.ProbeInterval <= TimeSpan.Zero || options.ProbeTimeout <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{ColibriOptions.SectionName}:{nameof(ColibriOptions.ProbeInterval)} and {nameof(ColibriOptions.ProbeTimeout)} must be positive.");
            }
            else if (options.ProbeTimeout >= options.ProbeInterval)
            {
                failures.Add(
                    $"{ColibriOptions.SectionName}:{nameof(ColibriOptions.ProbeTimeout)} must be shorter than {nameof(ColibriOptions.ProbeInterval)} (got {options.ProbeTimeout} >= {options.ProbeInterval}).");
            }

            if (options.UnhealthyThreshold < 1)
            {
                failures.Add(
                    $"{ColibriOptions.SectionName}:{nameof(ColibriOptions.UnhealthyThreshold)} must be >= 1 (got {options.UnhealthyThreshold}).");
            }
        }

        if (options.Serve.IsCatalog)
        {
            ValidateCatalog(options, multi, configuration, failures);
        }

        if (options.Serve.IsEnabled && !options.Serve.IsCatalog)
        {
            if (options.Serve.Port is < 1 or > 65535)
            {
                failures.Add($"{ColibriOptions.SectionName}:Serve:{nameof(ColibriServeOptions.Port)} must be a TCP port (got {options.Serve.Port}).");
            }

            // 93 D5, 67 D3's rule: a launched engine and a configured address are two answers to
            // where prompts go, and binder order is not how that gets decided.
            var baseUrl = configuration[$"{UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)}"]
                ?? configuration[$"{UpstreamBackendOptions.LegacySectionName}:{nameof(UpstreamBackendOptions.BaseUrl)}"];

            // Under Backend:Engines the engine's own BaseUrl is the other answer, and
            // BackendOptionsValidator is the one that compares the two.
            if (!multi && !string.IsNullOrWhiteSpace(baseUrl))
            {
                failures.Add(
                    $"{ColibriOptions.SectionName}:Serve:{nameof(ColibriServeOptions.Model)} and {UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)} "
                    + $"are both set. A launched engine listens on {options.LaunchedBaseUrl()}; set one or the other.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>97 D1: a catalogue is its own shape, and every way of half-writing it fails here.</summary>
    private static void ValidateCatalog(ColibriOptions options, bool multi, IConfiguration configuration, List<string> failures)
    {
        var serve = options.Serve;
        var key = $"{ColibriOptions.SectionName}:Serve";

        if (serve.IsEnabled)
        {
            failures.Add(
                $"{key}:{nameof(ColibriServeOptions.Model)} and {key}:{nameof(ColibriServeOptions.ModelsDir)}/{nameof(ColibriServeOptions.Models)} are both set. "
                + $"One model, or a catalogue: put that directory under ModelsDir or in Models. (The :colibri image sets Model; clear it with Colibri__Serve__Model=.)");
        }

        if (!string.IsNullOrWhiteSpace(serve.ModelId))
        {
            failures.Add($"{key}:{nameof(ColibriServeOptions.ModelId)} names the single Serve:Model; a catalogue model is named by its directory or its Models key.");
        }

        if (serve.MaxLoaded is < 1 or > ColibriOptions.MaxLoadedCeiling)
        {
            failures.Add($"{key}:{nameof(ColibriServeOptions.MaxLoaded)} must be between 1 and {ColibriOptions.MaxLoadedCeiling} (got {serve.MaxLoaded}).");
        }
        else if (serve.Port < 1 || serve.Port + serve.MaxLoaded - 1 > 65535)
        {
            failures.Add($"{key}:{nameof(ColibriServeOptions.Port)} {serve.Port} leaves no room for {serve.MaxLoaded} loaded model(s), one port each.");
        }

        if (serve.IdleUnload <= TimeSpan.Zero)
        {
            failures.Add($"{key}:{nameof(ColibriServeOptions.IdleUnload)} must be positive (got {serve.IdleUnload}).");
        }

        if (serve.LoadTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{key}:{nameof(ColibriServeOptions.LoadTimeout)} must be positive (got {serve.LoadTimeout}).");
        }

        foreach (var pair in serve.Models)
        {
            if (!ColibriOptions.IsModelName(pair.Key))
            {
                failures.Add($"{key}:Models:{pair.Key} is not a model name: letters, digits, '.', '_' and '-', up to 64.");
            }

            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                failures.Add($"{key}:Models:{pair.Key} names no directory.");
            }
        }

        var preload = serve.Preload.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (preload.Length > serve.MaxLoaded)
        {
            failures.Add($"{key}:{nameof(ColibriServeOptions.Preload)} names {preload.Length} models and {key}:MaxLoaded is {serve.MaxLoaded}; they cannot all stay loaded.");
        }

        var baseUrl = configuration[$"{UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)}"]
            ?? configuration[$"{UpstreamBackendOptions.LegacySectionName}:{nameof(UpstreamBackendOptions.BaseUrl)}"];

        if (!multi && !string.IsNullOrWhiteSpace(baseUrl))
        {
            failures.Add(
                $"{key}:{nameof(ColibriServeOptions.ModelsDir)} and {UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)} "
                + "are both set. A catalogue launches its own engines on loopback; set one or the other.");
        }
    }
}
