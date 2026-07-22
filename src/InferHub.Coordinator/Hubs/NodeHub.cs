using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Hubs;

public sealed class NodeHub(
    INodeRegistry registry,
    IDispatcher dispatcher,
    INodeConnectionTracker connections,
    NodeAuthFilter nodeAuth,
    IClusterMembership membership,
    IServiceProvider services,
    ILogger<NodeHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        if (!nodeAuth.IsAuthorized(Context))
        {
            Context.Abort();
            throw new HubException("unauthorized node");
        }

        // A standby must not accumulate a fleet it cannot dispatch to (phase 32). Refusing the
        // handshake is what makes node failover work at all: the node's retry loop rotates to the
        // next configured endpoint instead of sitting on a hub that will never send it a job.
        if (!membership.IsActive)
        {
            logger.LogInformation(
                "Refusing node connection {ConnectionId}: this coordinator is a standby",
                Context.ConnectionId);

            // Deliberately no Context.Abort() here, unlike the auth refusal above: aborting
            // terminates the connection before SignalR can deliver the reason, so the node sees a
            // bare close and cannot tell "this hub is a standby, try the next one" from "this hub
            // is broken". Throwing alone fails the client's StartAsync with this message, which is
            // what makes rotation immediate instead of a retry-delay away.
            throw new HubException("coordinator is a standby");
        }

        connections.Track(Context.ConnectionId, Context);
        return base.OnConnectedAsync();
    }

    public Task Register(NodeRegistration registration)
    {
        // Recognise any on-disk replicas the node reports BEFORE the registry fires its
        // Changed event, so the placement loop sees the existing holder and doesn't
        // schedule a full re-push for a replica the node already has on disk.
        var replication = services.GetService(typeof(ReplicationCoordinator)) as ReplicationCoordinator;
        replication?.ApplyInventory(Context.ConnectionId, registration.Replicas);

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

    // Do NOT add a CancellationToken parameter here. SignalR only treats a CancellationToken
    // as a synthetic (server-supplied) argument on methods that *return* a stream. This one
    // returns Task — it is a client-to-server upload — so the token would be counted as a
    // real argument the caller must send, and every invocation would die in the binder with
    // "Invocation provides 0 argument(s) but target expects 1", leaving the stream unbound
    // and the client hanging. Context.ConnectionAborted is the right token regardless.
    public async Task StreamChunks(IAsyncEnumerable<InferenceChunk> chunks)
    {
        // The node owns token production, so it uploads chunks to the hub as a
        // client-to-server stream; the dispatcher exposes them through a per-job channel.
        await foreach (var chunk in chunks.WithCancellation(Context.ConnectionAborted))
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

    // Node → hub upload of model-command progress (phase 26). Like StreamChunks this is a
    // client-to-server stream, so it must NOT declare a CancellationToken parameter — see the
    // StreamChunks comment above for why. Use Context.ConnectionAborted instead.
    public async Task StreamModelCommandProgress(IAsyncEnumerable<ModelCommandProgress> frames)
    {
        var commands = services.GetService(typeof(ModelCommandCoordinator)) as ModelCommandCoordinator;

        await foreach (var frame in frames.WithCancellation(Context.ConnectionAborted))
        {
            commands?.ReportProgress(frame);
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
        // Deliberately do NOT forget affinity here (phase 30). A disconnect is often a reconnect in
        // progress: the node comes back with a *new* connection id but the same stable node id, and
        // its warm conversations must survive that. Affinity now keys on the node id, so a hint for a
        // momentarily-absent node is a clean miss until the node returns or the sliding window lapses.
        await base.OnDisconnectedAsync(exception);
    }
}
