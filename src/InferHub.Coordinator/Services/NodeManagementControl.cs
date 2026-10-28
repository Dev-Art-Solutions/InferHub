using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 120: asks one node the questions its owner's app asks it — the catalogue, the estimate, an install — down the
/// connection it opened, as a tool job for that node alone. The node answers by its own terms (<c>Node:Management</c>);
/// the hub renders what it said and never decides for it (43 D1).
/// </summary>
public sealed class NodeManagementControl(INodeRegistry registry, IToolDispatcher tools, ILogger<NodeManagementControl> logger)
{
    /// <summary>What a node said: its payload (the control API's JSON) or a refusal with the status to render it as.</summary>
    public sealed record Answer(int Status, string? Payload, string? Error)
    {
        public bool Ok => Status == StatusCodes.Status200OK;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Answer> AskAsync(string nodeId, string operation, ManagementRequest request, CancellationToken cancellationToken)
    {
        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            return new Answer(StatusCodes.Status404NotFound, null, $"node '{nodeId}' not found");
        }

        if (node.Management is null)
        {
            // A node before 3.81 would answer the job as an unknown tool; the version says it plainly.
            return new Answer(StatusCodes.Status409Conflict, null, $"node '{node.NodeId}' ({node.Version}) is older than 3.81 and has no model management for its hub");
        }

        var job = new ToolJob(Guid.NewGuid(), ManagementJob.Capability, operation, JsonSerializer.Serialize(request, Json));
        var routable = new RoutableNode(node.ConnectionId, node.NodeId, node.Name);
        ToolResult result;

        try
        {
            result = await tools.DispatchToolAsync(routable, job, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            return new Answer(StatusCodes.Status504GatewayTimeout, null, ex.Message);
        }

        if (result.Success)
        {
            return new Answer(StatusCodes.Status200OK, result.Payload, null);
        }

        var status = ManagementJob.StatusOf(result.ErrorCode) ?? StatusCodes.Status502BadGateway;
        logger.LogInformation("Node {NodeId} refused {Operation} ({Status}): {Error}", node.NodeId, operation, status, result.Error);
        return new Answer(status, null, result.Error);
    }

    /// <summary>The pull a node agreed to install (<see cref="ManagementJob.Install"/>), read from its answer.</summary>
    public static ManagementInstall? ReadInstall(string? payload) =>
        payload is null ? null : JsonSerializer.Deserialize<ManagementInstall>(payload, Json);
}
