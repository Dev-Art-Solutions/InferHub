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
