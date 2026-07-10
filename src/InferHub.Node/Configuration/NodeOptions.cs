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
}

public sealed class ModelFilterOptions
{
    public List<string> Include { get; set; } = new();

    public List<string> Exclude { get; set; } = new();
}
