using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node;

public interface INodeIdentity
{
    string GetOrCreateNodeId();
}

public sealed class FileNodeIdentity(
    IHostEnvironment environment,
    IOptions<NodeOptions> options,
    ILogger<FileNodeIdentity> logger) : INodeIdentity
{
    public const string FileName = ".inferhub-node-id";

    public string GetOrCreateNodeId()
    {
        var baseDirectory = string.IsNullOrWhiteSpace(options.Value.DataDirectory)
            ? environment.ContentRootPath
            : options.Value.DataDirectory;

        Directory.CreateDirectory(baseDirectory);
        var path = Path.Combine(baseDirectory, FileName);

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
