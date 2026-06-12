namespace InferHub.Node;

public interface INodeIdentity
{
    string GetOrCreateNodeId();
}

public sealed class FileNodeIdentity(
    IHostEnvironment environment,
    ILogger<FileNodeIdentity> logger) : INodeIdentity
{
    public const string FileName = ".inferhub-node-id";

    public string GetOrCreateNodeId()
    {
        var path = Path.Combine(environment.ContentRootPath, FileName);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();

            if (Guid.TryParse(existing, out var parsed))
            {
                return parsed.ToString("D");
            }

            logger.LogWarning("Node identity file {Path} is invalid; generating a new node id", path);
        }

        var nodeId = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, nodeId);

        return nodeId;
    }
}
