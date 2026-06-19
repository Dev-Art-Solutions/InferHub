namespace InferHub.Node.Configuration;

public sealed class NodeOptions
{
    public const string SectionName = "Node";

    public string Name { get; set; } = Environment.MachineName;

    public int? MaxConcurrency { get; set; }

    public Dictionary<string, string> Labels { get; set; } = new();

    public ModelFilterOptions Models { get; set; } = new();
}

public sealed class ModelFilterOptions
{
    public List<string> Include { get; set; } = new();

    public List<string> Exclude { get; set; } = new();
}
