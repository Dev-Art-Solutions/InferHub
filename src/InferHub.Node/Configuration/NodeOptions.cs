namespace InferHub.Node.Configuration;

public sealed class NodeOptions
{
    public const string SectionName = "Node";

    public string Name { get; set; } = Environment.MachineName;

    public int? MaxConcurrency { get; set; }

    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>
    /// Directory for writable node state (the identity file). Null = ContentRootPath
    /// (default, unchanged behaviour). Set to e.g. C:\ProgramData\InferHub\Node when
    /// running as a service under a restricted account that cannot write next to the exe.
    /// </summary>
    public string? DataDirectory { get; set; }

    public ModelFilterOptions Models { get; set; } = new();

    /// <summary>What this node is allowed to be routed for (phase 40). Subtractive only.</summary>
    public CapabilityOptions Capabilities { get; set; } = new();

    /// <summary>How much of this box's card may be spent on models (phase 48). Unset = no gate.</summary>
    public VramOptions Vram { get; set; } = new();

    /// <summary>
    /// This box's own CPU/GPU ceiling (phase 82). Unset on both = no gate, byte-identical to v3.46.
    /// </summary>
    public ResourceLimitOptions ResourceLimits { get; set; } = new();
}

/// <summary>
/// <c>Node:ResourceLimits</c> — an operator's own cap on how much of this machine's CPU and GPU the
/// node may let itself be routed to consume, and the one ceiling in this file with no matching field
/// anywhere on <c>NodeProfile</c> (phase-82 D1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every other ceiling in this codebase — <c>Tools:Allowed</c>, <c>Node:MaxConcurrency</c>,
/// <c>Node:Vram</c> — has a matching key a hub-sent profile can narrow further, because a profile
/// narrowing a node's own configuration is still bounded by it (phase-43 D1). This one is not on the
/// wire at all.</b> The whole point of a per-box performance ceiling is that the operator who owns the
/// electricity bill gets the final word over a coordinator they may not fully trust — narrowing it
/// from the hub would be one thing; a hub that could <em>raise</em> it, even by omission, would not be
/// a ceiling.
/// </para>
/// <para>
/// <b>Soft by default, and that is deliberate</b> (phase-82 D2). Unset (both null) is today's exact
/// behaviour. Set one or both and this node polls its own CPU and GPU utilization every
/// <see cref="PollInterval"/>; <see cref="SustainedPolls"/> consecutive polls over either cap
/// withdraws the node from new placement — a meshed node the same way an unhealthy backend does
/// (phase 69), a solo node with the same 503 + Retry-After shape <see cref="LocalApi.LocalConcurrencyGate"/>
/// already uses. Work already in flight is never touched. <see cref="HardCpuCapPercent"/> is the
/// second, harder mechanism (phase-82 D6): a real OS-level CPU rate limit on this node's tool-worker
/// child processes, today on Windows only.
/// </para>
/// </remarks>
public sealed class ResourceLimitOptions
{
    /// <summary>
    /// The machine-wide CPU percentage (0-100) above which this node stops accepting new placement.
    /// Null = no CPU gate.
    /// </summary>
    public int? MaxCpuPercent { get; set; }

    /// <summary>
    /// The busiest visible GPU's utilization percentage (0-100) above which this node stops accepting
    /// new placement. Null = no GPU gate. Requires an NVIDIA driver NVML can see — Linux only today
    /// (the same platform scope as <see cref="Backends.CudaDeviceProbe"/>, phase-39 D5); set with no
    /// such device visible, this node logs it once at startup and the GPU half never trips.
    /// </summary>
    public int? MaxGpuPercent { get; set; }

    /// <summary>How often this node samples its own CPU/GPU usage. Default 5s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Consecutive over-cap polls required before this node withdraws from new placement. Default 3 —
    /// the same debounce shape as <c>Ollama:Supervisor:UnhealthyThreshold</c>, so one noisy sample
    /// does not take a box out of the fleet.
    /// </summary>
    public int SustainedPolls { get; set; } = 3;

    /// <summary>
    /// Consecutive under-cap polls required before a throttled node is routable again. Default 2 —
    /// deliberately allowed to differ from <see cref="SustainedPolls"/>, so recovery can be biased
    /// slower than tripping: a box still catching its breath is not somewhere new work belongs yet.
    /// </summary>
    public int RecoverPolls { get; set; } = 2;

    /// <summary>
    /// A real OS-level CPU rate cap (phase-82 D6), applied to this node's tool-worker child processes
    /// via a Windows Job Object. Null = off. Windows only today; set on another platform, this node
    /// logs it once at startup and runs with the soft cap alone.
    /// </summary>
    public int? HardCpuCapPercent { get; set; }

    public bool IsConfigured => MaxCpuPercent is not null || MaxGpuPercent is not null;
}

/// <summary>
/// <c>Node:Vram</c> — the arithmetic that decides whether a model fits, written where an operator
/// can see it instead of discovered from an out-of-memory error at 2am (phase-48 D1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Declared, not detected, and that is the decision this section turns on.</b> A node cannot
/// reliably measure the card it is on. Under WSL2 — which is where this project's own GPU box lives
/// — there are no <c>/dev/nvidia*</c> device nodes, the host's <c>nvidia-smi</c> cannot see the VM's
/// VRAM, and the only reliable signal that a GPU exists at all is that <c>libcuda.so.1</c> loads
/// (phase-39 D5). A node that guessed its own VRAM would guess wrong on the exact platform its
/// author develops on.
/// </para>
/// <para>
/// <b>Considered and rejected: detecting it and defaulting the budget to what was found.</b> It
/// works on bare-metal Linux, is wrong under WSL2, is wrong on a shared card, and is wrong the
/// moment somebody else's process is on the GPU. A budget that is usually right is worse than one
/// that is explicitly absent, because the first failure is an OOM inside somebody's job rather than
/// a startup message. The worker reports <c>torch.cuda.mem_get_info()</c> at startup so a
/// disagreement is <em>logged</em> — never so it can override the operator.
/// </para>
/// </remarks>
public sealed class VramOptions
{
    /// <summary>
    /// Total VRAM this node may plan around, in MiB. <b>Unset (0) means no gate</b> and is v3.15's
    /// behaviour exactly — a deployment that changes no config is unaffected by this whole phase.
    /// </summary>
    public int BudgetMiB { get; set; }

    /// <summary>
    /// Held back for the inference backend and the display, in MiB. Default 2048.
    /// </summary>
    /// <remarks>
    /// With <c>maxWorkers: 1</c> the common case is one recipe loaded at a time, so this key is
    /// really about the <em>second</em> thing on the card — an <c>:ollama</c> container holding a
    /// chat model beside the <c>:diffusion</c> one. The docs put that configuration in a warning box
    /// with the arithmetic written out.
    /// </remarks>
    public int ReserveMiB { get; set; } = 2048;

    /// <summary>What is actually available to models. Zero when no budget is declared.</summary>
    public int HeadroomMiB => BudgetMiB <= 0 ? 0 : BudgetMiB - Math.Max(0, ReserveMiB);
}

/// <summary>
/// Capabilities are declared by what the node actually runs, not configured by hand — so the only
/// knob is a subtractive one (phase-40 D2). <c>Disabled: ["chat"]</c> is how an operator says
/// "this box is for embeddings", which is the one thing the node cannot work out for itself.
/// </summary>
public sealed class CapabilityOptions
{
    public List<string> Disabled { get; set; } = new();

    public bool IsDisabled(string kind) =>
        Disabled.Any(disabled => string.Equals(disabled?.Trim(), kind, StringComparison.OrdinalIgnoreCase));
}

public sealed class ModelFilterOptions
{
    public List<string> Include { get; set; } = new();

    public List<string> Exclude { get; set; } = new();
}
