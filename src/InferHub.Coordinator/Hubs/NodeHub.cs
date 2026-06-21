using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Hubs;

public sealed class NodeHub(
    INodeRegistry registry,
    IDispatcher dispatcher,
    IConversationAffinity affinity,
    INodeConnectionTracker connections,
    NodeAuthFilter nodeAuth,
    ILogger<NodeHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        if (!nodeAuth.IsAuthorized(Context))
        {
            Context.Abort();
            throw new HubException("unauthorized node");
        }

        connections.Track(Context.ConnectionId, Context);
        return base.OnConnectedAsync();
    }

    public Task Register(NodeRegistration registration)
    {
        registry.Upsert(Context.ConnectionId, registration, DateTimeOffset.UtcNow);

        logger.LogInformation(
            "Node {NodeId} ({NodeName}) registered on connection {ConnectionId}",
            registration.NodeId,
            registration.Name,
            Context.ConnectionId);

        return Task.CompletedTask;
    }

    public Task Heartbeat(Heartbeat heartbeat)
    {
        if (!registry.Touch(Context.ConnectionId, heartbeat, DateTimeOffset.UtcNow))
        {
            logger.LogWarning(
                "Heartbeat received for unknown connection {ConnectionId} from node {NodeId}",
                Context.ConnectionId,
                heartbeat.NodeId);
        }

        return Task.CompletedTask;
    }

    public Task ReportModels(NodeModels models)
    {
        if (!registry.ReportModels(Context.ConnectionId, models, DateTimeOffset.UtcNow))
        {
            logger.LogWarning(
                "Model report received for unknown connection {ConnectionId} from node {NodeId}",
                Context.ConnectionId,
                models.NodeId);

            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Node {NodeId} reported {ModelCount} models on connection {ConnectionId}",
            models.NodeId,
            models.Models.Count,
            Context.ConnectionId);

        return Task.CompletedTask;
    }

    public Task JobResult(InferenceResult result)
    {
        if (!dispatcher.Complete(result))
        {
            logger.LogWarning(
                "Node connection {ConnectionId} returned result for unknown job {JobId}",
                Context.ConnectionId,
                result.JobId);
        }

        return Task.CompletedTask;
    }

    public async Task StreamChunks(
        IAsyncEnumerable<InferenceChunk> chunks,
        CancellationToken cancellationToken)
    {
        // The node owns token production, so it uploads chunks to the hub as a
        // client-to-server stream; the dispatcher exposes them through a per-job channel.
        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            if (!dispatcher.WriteChunk(chunk))
            {
                logger.LogWarning(
                    "Node connection {ConnectionId} streamed chunk for unknown job {JobId}",
                    Context.ConnectionId,
                    chunk.JobId);
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Forget(Context.ConnectionId);

        if (registry.Remove(Context.ConnectionId))
        {
            logger.LogInformation("Node connection {ConnectionId} disconnected", Context.ConnectionId);
        }

        dispatcher.FailForConnection(Context.ConnectionId, exception);
        affinity.ForgetConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
