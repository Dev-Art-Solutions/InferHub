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

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Model);

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
        if (backend.Value.Normalized() != BackendOptions.Colibri)
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

        if (options.Serve.IsEnabled)
        {
            if (options.Serve.Port is < 1 or > 65535)
            {
                failures.Add($"{ColibriOptions.SectionName}:Serve:{nameof(ColibriServeOptions.Port)} must be a TCP port (got {options.Serve.Port}).");
            }

            // 93 D5, 67 D3's rule: a launched engine and a configured address are two answers to
            // where prompts go, and binder order is not how that gets decided.
            var baseUrl = configuration[$"{UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)}"]
                ?? configuration[$"{UpstreamBackendOptions.LegacySectionName}:{nameof(UpstreamBackendOptions.BaseUrl)}"];

            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                failures.Add(
                    $"{ColibriOptions.SectionName}:Serve:{nameof(ColibriServeOptions.Model)} and {UpstreamBackendOptions.SectionName}:{nameof(UpstreamBackendOptions.BaseUrl)} "
                    + $"are both set. A launched engine listens on {options.LaunchedBaseUrl()}; set one or the other.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
