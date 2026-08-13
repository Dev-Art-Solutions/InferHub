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

    /// <summary>
    /// A node asking what it should be doing (phase 43, D2). The pull direction: the hub does not
    /// track who has which revision, because whoever comes back asks the question again and the
    /// answer is the same document.
    /// </summary>
    /// <remarks>
    /// It returns a value, which is fine — the binder trap on <c>StreamChunks</c> is specific to
    /// client-to-server <em>streams</em>, and this is an ordinary invocation with a result.
    /// </remarks>
    public Task<NodeProfileAssignment> RequestNodeProfile(string nodeId)
    {
        if (services.GetService(typeof(IProfileRegistry)) is not IProfileRegistry profiles)
        {
            return Task.FromResult(NodeProfileAssignment.None);
        }

        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.ConnectionId, Context.ConnectionId, StringComparison.Ordinal));

        var assignment = profiles.MatchFor(nodeId, node?.Labels);

        if (assignment.IsConflict)
        {
            logger.LogWarning(
                "Node {NodeId} asked for its profile and matches {Count} of them ({Profiles}); sending none",
                nodeId,
                assignment.Conflicts!.Count,
                string.Join(", ", assignment.Conflicts!));
        }

        // Phase 44, D1. Ownership is recorded on the pull as well as on the push, so a hub that
        // restarted knows which names belong to which box the moment that box comes back — before
        // any replication pass could target one of them.
        if (services.GetService(typeof(CollectionOwnership)) is CollectionOwnership ownership)
        {
            if (!assignment.IsConflict && assignment.Profile?.Retrieval is { Enabled: true } retrieval)
            {
                ownership.Assign(nodeId, retrieval.Collections);
            }
            else
            {
                ownership.Release(nodeId);
            }
        }

        return Task.FromResult(assignment);
    }

    /// <summary>
    /// What a node says about the corpus it hosts (phase 44, D6). The hub records it and never asks
    /// for it: querying a node's corpus to build a status page would make <c>/api/status</c> a
    /// synchronous dependency on a box that may be asleep.
    /// </summary>
    public Task ReportCorpusState(NodeCorpusState state)
    {
        if (services.GetService(typeof(NodeCorpusRegistry)) is NodeCorpusRegistry corpora)
        {
            corpora.Report(state);
        }

        if (state is { Status: NodeCorpusState.Failed, Error: { Length: > 0 } error })
        {
            logger.LogWarning(
                "Node {NodeId} reports its corpus is not running: {Error}",
                state.NodeId,
                error);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// What a node's tool runtime is doing (phase 45). Recorded, never asked for — the same mailbox
    /// as <see cref="ReportCorpusState"/>, and for the same reason.
    /// </summary>
    public Task ReportToolState(NodeToolState state)
    {
        if (services.GetService(typeof(NodeToolRegistry)) is NodeToolRegistry tools)
        {
            tools.Report(state);
        }

        return Task.CompletedTask;
    }

    /// <summary>What a node did with its profile, including everything it refused and why (D6).</summary>
    public Task ReportProfileState(NodeProfileState state)
    {
        if (services.GetService(typeof(IProfileRegistry)) is IProfileRegistry profiles)
        {
            profiles.ReportState(state);
        }

        // The clamped concurrency cap lands on the registry entry, so lowering a node's cap does not
        // need it to re-register — the router reads the effective number on the next dispatch.
        registry.SetEffectiveConcurrency(Context.ConnectionId, state.MaxConcurrency);

        if (state.Refusals.Count > 0
            && services.GetService(typeof(IAuditLog)) is IAuditLog audit)
        {
            // The audit log is the per-node last-admin-action store, and a profile application is
            // exactly that category (D5) — unlike phase-22 D5's cloud-burst events, which were kept
            // out of it precisely because they were not per-node admin actions.
            audit.Record(
                state.NodeId,
                $"profile.refused:{state.ProfileName}@{state.Revision} ({state.Refusals.Count})",
                "node",
                state.AtUtc);
        }

        logger.LogInformation(
            "Node {NodeId} reports profile '{Profile}' revision {Revision}: {Status}, {Applied} applied, {Refused} refused, {Pending} pending",
            state.NodeId,
            state.ProfileName ?? "(none)",
            state.Revision,
            state.Status(),
            state.Applied.Count,
            state.Refusals.Count,
            state.Pending.Count);

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

    public Task ToolJobResult(ToolResult result)
    {
        var tools = services.GetService(typeof(IToolDispatcher)) as IToolDispatcher;

        if (tools?.CompleteTool(result) is not true)
        {
            logger.LogWarning(
                "Node connection {ConnectionId} returned result for unknown tool job {JobId}",
                Context.ConnectionId,
                result.JobId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One progress frame for a tool job that is <em>not</em> streaming (phase 47, D2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An ordinary invocation carrying a <see cref="ToolChunk"/>, not a stream — so the binder trap
    /// that has bitten <c>StreamChunks</c>, <c>StreamToolChunks</c> and
    /// <c>StreamModelCommandProgress</c> does not apply, and this method deliberately declares no
    /// <c>CancellationToken</c> either, because the next reader should not have to work out which
    /// of those two rules they are looking at.
    /// </para>
    /// <para>
    /// It is not a new transport: it is the connection the node already opened, carrying the
    /// contract the tool runtime already had. What it is <b>not</b> is <c>/api/admin/stream</c> —
    /// image progress is client-facing, and an admin channel carrying tenants' job ids is an
    /// authorization mistake waiting for somebody to notice.
    /// </para>
    /// </remarks>
    public Task ToolJobProgress(ToolChunk chunk)
    {
        var tools = services.GetService(typeof(IToolDispatcher)) as IToolDispatcher;

        // Deliberately quiet when nobody is listening: a job whose watcher has gone away, or one
        // that finished a beat before its last progress frame arrived, is the ordinary case.
        tools?.WriteToolChunk(chunk);
        return Task.CompletedTask;
    }

    // Node → hub upload of tool chunks (phase 41). The THIRD method to hit the binder trap, so it
    // is written out once more rather than left to the reader: this must NOT declare a
    // CancellationToken parameter — see StreamChunks above. Use Context.ConnectionAborted.
    public async Task StreamToolChunks(IAsyncEnumerable<ToolChunk> chunks)
    {
        var tools = services.GetService(typeof(IToolDispatcher)) as IToolDispatcher;

        await foreach (var chunk in chunks.WithCancellation(Context.ConnectionAborted))
        {
            if (tools?.WriteToolChunk(chunk) is not true)
            {
                logger.LogWarning(
                    "Node connection {ConnectionId} streamed a chunk for unknown tool job {JobId}",
                    Context.ConnectionId,
                    chunk.JobId);
            }
        }
    }

    /// <summary>
    /// The bytes of a job the node was told carries a streamed attachment (phase 53, D1). The node
    /// invokes this and writes what comes back straight into its scratch directory; the hub reads
    /// them off the client's live request body one window at a time and never holds the body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a server-to-client stream, and it is the first hub method here where a
    /// <c>CancellationToken</c> parameter is correct.</b> The binder trap written out three times
    /// above applies to client-to-server streams — methods that return <c>Task</c> and take an
    /// <c>IAsyncEnumerable</c> argument — where SignalR counts a token as a real argument the caller
    /// never sends. A method that <em>returns</em> a stream is the shape SignalR supplies the token
    /// for synthetically, so this one is bound correctly and the token really does fire when the
    /// node walks away. Do not delete it on the strength of the other three comments.
    /// </para>
    /// <para>
    /// The node pulls rather than the hub pushing, so phase-26 D1 is untouched: the stream is
    /// established by the node's own invocation on the connection it opened, exactly as
    /// <c>RequestNodeProfile</c> is, and the hub still never dials a node.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<AttachmentChunk> StreamAttachments(Guid jobId, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(IToolDispatcher)) is not IToolDispatcher tools)
        {
            return AsyncEnumerable.Empty<AttachmentChunk>();
        }

        return tools.ReadUploadAsync(jobId, cancellationToken);
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
