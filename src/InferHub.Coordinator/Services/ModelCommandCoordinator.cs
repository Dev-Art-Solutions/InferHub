using System.Collections.Concurrent;
using InferHub.Coordinator.Hubs;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Owns hub-issued model commands (phase 26): it sends <see cref="ModelCommand"/> down a node's
/// connection, coalesces a duplicate command for the same node+kind+model onto the one already
/// running, tracks the latest progress frame, and raises <see cref="ProgressReceived"/> so the SSE
/// admin stream can relay it. It holds no persistent state — a coordinator restart forgets in-flight
/// commands, like everything else on the hub.
/// </summary>
public sealed class ModelCommandCoordinator(
    IHubContext<NodeHub> hubContext,
    INodeRegistry registry,
    ILogger<ModelCommandCoordinator> logger)
{
    private readonly ConcurrentDictionary<CommandKey, Guid> active = new();
    private readonly ConcurrentDictionary<Guid, ModelCommandProgress> latest = new();

    /// <summary>Raised for every progress frame a node streams back. The SSE relay subscribes to this.</summary>
    public event Action<ModelCommandProgress>? ProgressReceived;

    public IReadOnlyCollection<ModelCommandProgress> ActiveCommands => latest.Values.ToArray();

    public sealed record StartResult(Guid CommandId, bool Reused);

    /// <summary>
    /// Send a command to a node, or — if an identical one (same node, kind, model) is already
    /// running — return that command's id instead of starting a second. Throws if the node is not
    /// connected. Returns <c>null</c> if the node is unknown.
    /// </summary>
    public async Task<StartResult?> SendAsync(string nodeId, string kind, string model, CancellationToken cancellationToken)
    {
        var connectionId = registry.FindConnectionIdByNodeId(nodeId);
        if (connectionId is null)
        {
            return null;
        }

        var key = new CommandKey(nodeId, kind, model);
        if (active.TryGetValue(key, out var existing))
        {
            logger.LogInformation("Coalescing {Kind} '{Model}' on node {NodeId} onto command {CommandId}", kind, model, nodeId, existing);
            return new StartResult(existing, Reused: true);
        }

        var commandId = Guid.NewGuid();
        if (!active.TryAdd(key, commandId))
        {
            // Lost a race with a concurrent identical request; ride its command.
            return new StartResult(active[key], Reused: true);
        }

        var command = new ModelCommand(commandId, kind, model);
        latest[commandId] = new ModelCommandProgress(commandId, nodeId, kind, model, "queued", null, Done: false, Error: null);

        try
        {
            await hubContext.Clients.Client(connectionId).SendAsync("ExecuteModelCommand", command, cancellationToken);
            logger.LogInformation("Sent {Kind} model command {CommandId} for '{Model}' to node {NodeId}", kind, commandId, model, nodeId);
            return new StartResult(commandId, Reused: false);
        }
        catch
        {
            active.TryRemove(key, out _);
            latest.TryRemove(commandId, out _);
            throw;
        }
    }

    /// <summary>Called by the hub for each progress frame a node streams back.</summary>
    public void ReportProgress(ModelCommandProgress progress)
    {
        latest[progress.CommandId] = progress;

        if (progress.Done)
        {
            active.TryRemove(new CommandKey(progress.NodeId, progress.Kind, progress.ModelName), out _);
            // Keep the terminal frame briefly discoverable, then forget it — a restart forgets anyway.
            _ = ForgetLaterAsync(progress.CommandId);
        }

        ProgressReceived?.Invoke(progress);
    }

    private async Task ForgetLaterAsync(Guid commandId)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        latest.TryRemove(commandId, out _);
    }

    private readonly record struct CommandKey(string NodeId, string Kind, string Model);
}
