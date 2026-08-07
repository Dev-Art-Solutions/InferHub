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
