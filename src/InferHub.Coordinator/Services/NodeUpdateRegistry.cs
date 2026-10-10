using System.Collections.Concurrent;
using InferHub.Coordinator.Hubs;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about its version and the newest release it could move to (phase 101) —
/// phase 45's registry once more. In memory (rule 4): a coordinator restart forgets it and every node
/// re-reports at registration.
/// </summary>
public sealed class NodeUpdateRegistry
{
    private readonly ConcurrentDictionary<string, NodeUpdateState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeUpdateState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeUpdateState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}

/// <summary>
/// Sends a node a check or an apply (phase 101, D6), after refusing from what the node last said. The node
/// refuses again for itself (43 D1); this copy exists for the message, so an admin reads "its operator
/// updates it by hand" as a 409 rather than a 202 followed by nothing.
/// </summary>
public sealed class NodeUpdateControl(
    INodeRegistry registry,
    NodeUpdateRegistry updates,
    IHubContext<NodeHub> hub,
    IAuditLog audit,
    ILogger<NodeUpdateControl> logger)
{
    public const string NotFound = "not-found";
    public const string Refused = "refused";

    public async Task<(string? Refusal, string Message, NodeUpdateState? State)> SendAsync(
        string nodeId,
        string kind,
        string by,
        CancellationToken cancellationToken)
    {
        var connectionId = registry.FindConnectionIdByNodeId(nodeId);

        if (connectionId is null)
        {
            return (NotFound, $"node '{nodeId}' not found", null);
        }

        var state = updates.Of(nodeId);

        if (Refuse(nodeId, kind, state) is { } refusal)
        {
            return (Refused, refusal, state);
        }

        await hub.Clients.Client(connectionId).SendAsync("NodeUpdate", new NodeUpdateCommand(kind, by), cancellationToken);
        audit.Record(nodeId, $"update.{kind}", by, DateTimeOffset.UtcNow);
        logger.LogInformation("Sent update {Kind} to node {NodeId} (by {By})", kind, nodeId, by);

        return (null, kind == NodeUpdateCommand.KindApply
            ? $"node '{nodeId}' is updating{(state?.Available is { } target ? $" to {target}" : string.Empty)}; it restarts when the setup finishes"
            : $"node '{nodeId}' is checking for a release", state);
    }

    /// <summary>The hub's copy of the node's rules, pure so the tests can walk every branch.</summary>
    public static string? Refuse(string nodeId, string kind, NodeUpdateState? state)
    {
        if (kind is not (NodeUpdateCommand.KindCheck or NodeUpdateCommand.KindApply))
        {
            return $"'{kind}' is not an update command (check or apply)";
        }

        if (state is null)
        {
            return $"node '{nodeId}' has not reported an update state: it runs a release before v3.66, which updates by hand";
        }

        if (kind == NodeUpdateCommand.KindCheck)
        {
            return state.Check || state.AllowFromHub
                ? null
                : $"node '{nodeId}' has Update:Check and Update:AllowFromHub off: it looks for no releases";
        }

        if (!state.AllowFromHub)
        {
            return $"node '{nodeId}' has Update:AllowFromHub off: its operator updates it by hand";
        }

        if (!state.CanApply)
        {
            return $"node '{nodeId}' cannot apply updates: {state.WhyNot}";
        }

        if (state.State is NodeUpdatePhase.Downloading or NodeUpdatePhase.Applying)
        {
            return $"node '{nodeId}' is already {state.State} {state.Available}";
        }

        // Not refused for "nothing available": the node checks first when it knows of none, and a hub whose
        // last report is six hours old should not overrule a release published since.
        return null;
    }
}
