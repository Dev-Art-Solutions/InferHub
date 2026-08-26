using System.Reflection;
using System.Collections.Concurrent;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Capabilities;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Retrieval;
using InferHub.Node.Tools;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector.Replication;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace InferHub.Node;

public sealed class CoordinatorConnection(
    IOptions<CoordinatorOptions> coordinatorOptions,
    IOptions<NodeOptions> nodeOptions,
    INodeIdentity nodeIdentity,
    IInferenceBackend backend,
    InferenceExecutor inferenceExecutor,
    ModelCommandExecutor modelCommandExecutor,
    ToolExecutor toolExecutor,
    IToolRuntime toolRuntime,
    IOptions<ToolOptions> toolOptionsAccessor,
    NodeProfileApplier profiles,
    RetrievalHost retrieval,
    ReplicaStore replicaStore,
    IBackendSupervisor supervisor,
    ILogger<CoordinatorConnection> logger) : IAsyncDisposable
{
    private readonly CoordinatorOptions coordinator = coordinatorOptions.Value;
    private readonly NodeOptions node = nodeOptions.Value;
    private readonly ToolOptions toolOptions = toolOptionsAccessor.Value;

    private readonly SemaphoreSlim reconnectLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string nodeId = nodeIdentity.GetOrCreateNodeId();
    private readonly IReadOnlyList<string> endpoints = coordinatorOptions.Value.ResolvedEndpoints();
    private int endpointIndex;
    private HubConnection? connection;
    private Task? heartbeatTask;
    private Task? modelRefreshTask;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeJobs = new();
    private int inFlight;
    private bool subscribedToSupervisor;
    private bool subscribedToTools;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeToSupervisor();
        SubscribeToTools();
        return ConnectUntilSuccessfulAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        UnsubscribeFromSupervisor();
        UnsubscribeFromTools();
        await lifetime.CancelAsync();

        if (heartbeatTask is not null)
        {
            try
            {
                await heartbeatTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (modelRefreshTask is not null)
        {
            try
            {
                await modelRefreshTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (connection is not null)
        {
            await connection.StopAsync(cancellationToken);
            await connection.DisposeAsync();
            connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        UnsubscribeFromSupervisor();
        UnsubscribeFromTools();
        await lifetime.CancelAsync();
        reconnectLock.Dispose();
        lifetime.Dispose();

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
    }

    private void SubscribeToSupervisor()
    {
        if (!supervisor.IsSupervising || subscribedToSupervisor)
        {
            return;
        }

        subscribedToSupervisor = true;
        supervisor.Recovered += OnBackendRecovered;
        supervisor.Restarting += OnBackendRestarting;
    }

    private void UnsubscribeFromSupervisor()
    {
        if (!subscribedToSupervisor)
        {
            return;
        }

        subscribedToSupervisor = false;
        supervisor.Recovered -= OnBackendRecovered;
        supervisor.Restarting -= OnBackendRestarting;
    }

    private void SubscribeToTools()
    {
        if (!toolRuntime.Enabled || subscribedToTools)
        {
            return;
        }

        subscribedToTools = true;
        toolRuntime.CapabilitiesChanged += OnToolCapabilitiesChanged;
    }

    private void UnsubscribeFromTools()
    {
        if (!subscribedToTools)
        {
            return;
        }

        subscribedToTools = false;
        toolRuntime.CapabilitiesChanged -= OnToolCapabilitiesChanged;
    }

    /// <summary>
    /// A tool pool that has given up withdraws its capabilities, and a pool that recovers restores
    /// them. Either way the coordinator has to be told at once rather than on the next model
    /// refresh — phase-36 D7's <c>Recovered</c> event, for the same reason: up to a minute of the
    /// hub routing transcriptions at a node that stopped transcribing.
    /// </summary>
    private void OnToolCapabilitiesChanged()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReportModelsAsync(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not re-report capabilities after a tool changed state");
            }
        });
    }

    /// <summary>
    /// A broken backend reports zero models, which is what unroutes this node — so recovery has
    /// to push a fresh report rather than wait out <c>ModelRefreshInterval</c> (up to a minute of
    /// a healthy node sitting out of the fleet).
    /// </summary>
    private void OnBackendRecovered()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReportModelsAsync(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not re-report models after the backend recovered");
            }
        });
    }

    /// <summary>
    /// The in-flight count lives here, so the cost of a restart is recorded by the thing that
    /// knows it. It is not <em>protected</em> — see the supervisor for why waiting to drain would
    /// be worse.
    /// </summary>
    private void OnBackendRestarting(BackendHealth health)
    {
        var running = Volatile.Read(ref inFlight);

        if (running == 0)
        {
            return;
        }

        logger.LogWarning(
            "The backend is {Health} and about to be restarted; {InFlight} in-flight job(s) will be lost.",
            health,
            running);
    }

    private HubConnection BuildConnection(string coordinatorUrl)
    {
        var hubUrl = BuildHubUrl(coordinatorUrl);
        var enrollmentSecret = coordinator.EnrollmentSecret;

        if (string.IsNullOrWhiteSpace(enrollmentSecret))
        {
            logger.LogWarning(
                "Coordinator:EnrollmentSecret is not configured; the coordinator will refuse this node.");
        }

        var builder = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrWhiteSpace(enrollmentSecret))
                {
                    options.Headers["X-Node-Enrollment-Secret"] = enrollmentSecret;
                }
            });

        // With a single coordinator, SignalR's automatic reconnect is exactly right and stays.
        // With an HA list it is not: it would spend its backoff schedule retrying the hub that
        // just lost the lease, while the node's own rotation is what actually finds the new
        // active one. Reconnect there is ours (phase 32).
        if (!coordinator.HasFailoverEndpoints())
        {
            builder = builder.WithAutomaticReconnect();
        }

        return builder.Build();
    }

    private void RegisterConnectionHandlers(HubConnection hubConnection)
    {
        hubConnection.On<InferenceJob>("RunJob", RunJobAsync);
        hubConnection.On<InferenceJob>("RunStreamingJob", RunStreamingJobAsync);
        hubConnection.On<ModelCommand>("ExecuteModelCommand", RunModelCommandAsync);
        hubConnection.On<ToolJob>("ExecuteToolJob", RunToolJobAsync);
        hubConnection.On<ToolJob>("ExecuteStreamingToolJob", RunStreamingToolJobAsync);
        hubConnection.On<NodeProfile>("ApplyNodeProfile", OnApplyNodeProfile);
        hubConnection.On("ClearNodeProfile", () => OnApplyNodeProfile(null));
        hubConnection.On<Guid>("CancelJob", CancelJob);
        hubConnection.On<VectorReplicaAssignment>("AssignVectorReplica", OnAssignVectorReplica);
        hubConnection.On<VectorReplicaOp>("ApplyVectorOp", OnApplyVectorOp);
        hubConnection.On<string>("DropVectorReplica", OnDropVectorReplica);

        hubConnection.Reconnecting += error =>
        {
            logger.LogWarning(error, "Coordinator connection lost; reconnecting");
            return Task.CompletedTask;
        };

        hubConnection.Reconnected += async connectionId =>
        {
            logger.LogInformation("Coordinator connection reconnected as {ConnectionId}", connectionId);
            await RegisterAsync(lifetime.Token);
        };

        hubConnection.Closed += async error =>
        {
            if (lifetime.IsCancellationRequested)
            {
                return;
            }

            // Rotation disposes the connection it replaced, which fires this. Only the *current*
            // connection closing is a reason to reconnect; otherwise a successful failover would
            // immediately kick off a second connect loop against the hub it just left.
            if (!ReferenceEquals(Volatile.Read(ref connection), hubConnection))
            {
                return;
            }

            logger.LogWarning(error, "Coordinator connection closed; retrying");
            await ConnectUntilSuccessfulAsync(lifetime.Token);
        };
    }

    private async Task ConnectUntilSuccessfulAsync(CancellationToken cancellationToken)
    {
        await reconnectLock.WaitAsync(cancellationToken);

        try
        {
            var attemptsSinceDelay = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (connection is { State: HubConnectionState.Connected })
                {
                    return;
                }

                var url = endpoints[endpointIndex];

                try
                {
                    logger.LogInformation("Connecting to coordinator {CoordinatorUrl}", url);
                    var candidate = BuildConnection(url);
                    RegisterConnectionHandlers(candidate);

                    try
                    {
                        await candidate.StartAsync(cancellationToken);
                    }
                    catch
                    {
                        await candidate.DisposeAsync();
                        throw;
                    }

                    await ReplaceConnectionAsync(candidate);
                    await RegisterAsync(cancellationToken);
                    EnsureHeartbeatLoop();
                    EnsureModelRefreshLoop();
                    logger.LogInformation("Connected to coordinator {CoordinatorUrl}", url);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A standby refuses the handshake, so a failure here is the normal way a node
                    // discovers which hub is leading — try the next one immediately, and only
                    // back off once the whole list has been tried (phase 32).
                    endpointIndex = (endpointIndex + 1) % endpoints.Count;
                    attemptsSinceDelay++;

                    if (attemptsSinceDelay < endpoints.Count)
                    {
                        logger.LogWarning(ex, "Coordinator {CoordinatorUrl} did not accept this node; trying the next one", url);
                        continue;
                    }

                    attemptsSinceDelay = 0;
                    logger.LogWarning(ex, "Could not connect to coordinator; retrying in {DelaySeconds}s", coordinator.RetryDelay.TotalSeconds);
                    await Task.Delay(coordinator.RetryDelay, cancellationToken);
                }
            }
        }
        finally
        {
            reconnectLock.Release();
        }
    }

    private async Task ReplaceConnectionAsync(HubConnection candidate)
    {
        var previous = Interlocked.Exchange(ref connection, candidate);

        if (previous is null || ReferenceEquals(previous, candidate))
        {
            return;
        }

        try
        {
            await previous.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not dispose the previous coordinator connection");
        }
    }

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new InvalidOperationException("Coordinator connection has not been built.");
        }

        var inventory = replicaStore.Inventory();
        var registration = new NodeRegistration(
            nodeId,
            node.Name,
            backend.Endpoint,
            GetVersion(),
            node.Labels.Count == 0 ? null : new Dictionary<string, string>(node.Labels),
            // The clamped cap, not the configured one: a node that reconnects while a profile has
            // lowered its concurrency must not spend one registration advertising the higher number.
            profiles.Effective.MaxConcurrency,
            inventory.Count == 0 ? null : inventory,
            backend.SupportsModelManagement,
            // Capabilities are declared on the model report, not here (phase-40 D1's note).
            Capabilities: null,
            // This one *is* declared here, because unlike a capability it does not follow from
            // asking the backend what it holds — it is one key on this box (phase-53 D5), and a
            // hub needs the answer before the first job, not after the first model refresh.
            SupportsStreamedAttachments: toolOptions.SupportsStreamedAttachments);

        await connection.InvokeAsync("Register", registration, cancellationToken);
        await RequestProfileAsync(cancellationToken);
        await ReportModelsAsync(cancellationToken);

        logger.LogInformation(
            "Registered node {NodeId} ({NodeName}) with coordinator",
            registration.NodeId,
            registration.Name);
    }

    /// <summary>
    /// Phase 43, D2. The node <em>asks</em> at registration rather than waiting to be told, so a hub
    /// that never learned this node was away — a reboot, a network partition, a coordinator restart
    /// — does not have to track who has what. Desired state converges because whoever comes back
    /// asks the question again.
    /// </summary>
    private async Task RequestProfileAsync(CancellationToken cancellationToken)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        NodeProfileAssignment? assignment;

        try
        {
            assignment = await activeConnection.InvokeAsync<NodeProfileAssignment?>(
                "RequestNodeProfile",
                nodeId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // A hub older than v3.11 has no such method, and a node that refused to register over
            // that would be exactly the mixed-fleet outage phase-40 D1 exists to avoid.
            logger.LogDebug(ex, "This coordinator did not answer a profile request; running local configuration");
            return;
        }

        if (assignment is null || assignment.IsConflict)
        {
            if (assignment?.IsConflict is true)
            {
                logger.LogWarning(
                    "The coordinator reports that {Count} profiles match this node ({Profiles}); it has sent none and this node keeps what it is running. Fix the selectors.",
                    assignment.Conflicts!.Count,
                    string.Join(", ", assignment.Conflicts!));
            }

            return;
        }

        await ApplyProfileAsync(assignment.Profile, cancellationToken);
    }

    /// <summary>A live push: an operator wrote or deleted a profile that matches this node.</summary>
    private void OnApplyNodeProfile(NodeProfile? profile)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyProfileAsync(profile, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not apply the node profile the coordinator sent");
            }
        });
    }

    /// <summary>
    /// Clamp it, apply it, say what happened, and run whatever model commands survived. Refusals are
    /// per item and never all-or-nothing (D6): a profile that asks for one impossible thing and four
    /// possible ones applies the four.
    /// </summary>
    private async Task ApplyProfileAsync(NodeProfile? profile, CancellationToken cancellationToken)
    {
        var application = await profiles.ApplyAsync(nodeId, profile, cancellationToken);
        var activeConnection = connection;

        if (activeConnection is { State: HubConnectionState.Connected })
        {
            try
            {
                await activeConnection.InvokeAsync("ReportProfileState", application.State, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not report profile state to the coordinator");
            }
        }

        // Phase 44, D6. Say what the corpus is doing right after a profile touched it, rather than
        // waiting for the periodic report: an operator who just switched retrieval on is looking at
        // the console now, and "pending" for thirty seconds reads as "it did not work".
        await ReportCorpusStateAsync(cancellationToken);

        // Phase 45, and a profile is the *most* likely thing to have just suspended a pool — so the
        // console shows `suspended` rather than `running` for the next refresh interval.
        await ReportToolStateAsync(cancellationToken);

        if (application.Changed)
        {
            // The declaration is what unroutes this node for a capability the profile switched off,
            // so it has to go out at once rather than on the next refresh — the phase-36 D7
            // mechanism again, for the third reason.
            await ReportModelsAsync(cancellationToken);
        }

        if (application.Commands.Count == 0)
        {
            return;
        }

        // Model commands go down the one path that already exists for them (phase 26), progress
        // frames and all. A second pull path is a second set of bugs.
        //
        // Not awaited, and that is the point: a profile arrives during registration, and a pull is
        // minutes. Waiting for one here would hold up the connection that is meant to be carrying
        // its progress — the profile reports them as `pending` precisely so the answer can come
        // later.
        _ = Task.Run(async () =>
        {
            foreach (var command in application.Commands)
            {
                try
                {
                    await RunModelCommandAsync(command);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Profile-driven {Kind} of '{Model}' failed", command.Kind, command.ModelName);
                }
            }
        });
    }

    private void EnsureHeartbeatLoop()
    {
        if (heartbeatTask is { IsCompleted: false })
        {
            return;
        }

        heartbeatTask = Task.Run(SendHeartbeatsAsync);
    }

    private void EnsureModelRefreshLoop()
    {
        if (modelRefreshTask is { IsCompleted: false })
        {
            return;
        }

        modelRefreshTask = Task.Run(SendModelReportsAsync);
    }

    private async Task SendHeartbeatsAsync()
    {
        using var timer = new PeriodicTimer(coordinator.HeartbeatInterval);

        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync(lifetime.Token);
                await timer.WaitForNextTickAsync(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Heartbeat failed");

                try
                {
                    await timer.WaitForNextTickAsync(lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (connection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        // Phase 69. `Health` is null until the supervisor has made up its mind, and null on a node
        // that watches nothing — both travel as "no opinion", which the hub reads as routable.
        var heartbeat = new Heartbeat(
            nodeId,
            DateTimeOffset.UtcNow,
            Volatile.Read(ref inFlight),
            supervisor.Health);

        await connection.InvokeAsync("Heartbeat", heartbeat, cancellationToken);
    }

    private async Task RunJobAsync(InferenceJob job)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            logger.LogWarning("Received job {JobId} while not connected to coordinator", job.JobId);
            return;
        }

        Interlocked.Increment(ref inFlight);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

        try
        {
            activeJobs[job.JobId] = jobCts;
            logger.LogInformation("Running {JobKind} job {JobId}", job.Kind, job.JobId);
            var result = await inferenceExecutor.RunAsync(job, jobCts.Token);

            if (activeConnection.State is HubConnectionState.Connected)
            {
                await activeConnection.InvokeAsync("JobResult", result, jobCts.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not return result for job {JobId}", job.JobId);
        }
        finally
        {
            activeJobs.TryRemove(job.JobId, out _);
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task RunStreamingJobAsync(InferenceJob job)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            logger.LogWarning("Received streaming job {JobId} while not connected to coordinator", job.JobId);
            return;
        }

        Interlocked.Increment(ref inFlight);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

        try
        {
            activeJobs[job.JobId] = jobCts;
            logger.LogInformation("Running streaming {JobKind} job {JobId}", job.Kind, job.JobId);

            await activeConnection.InvokeAsync(
                "StreamChunks",
                inferenceExecutor.StreamAsync(job, jobCts.Token),
                jobCts.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || jobCts.IsCancellationRequested)
        {
            logger.LogInformation("Streaming job {JobId} was canceled", job.JobId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not stream result for job {JobId}", job.JobId);
        }
        finally
        {
            activeJobs.TryRemove(job.JobId, out _);
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task RunModelCommandAsync(ModelCommand command)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            logger.LogWarning("Received model command {CommandId} while not connected", command.CommandId);
            return;
        }

        using var commandCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

        try
        {
            logger.LogInformation("Executing {Kind} model command {CommandId}", command.Kind, command.CommandId);

            // Upload the progress frames as a client-to-server stream, exactly like StreamChunks —
            // the same reason the hub method must not declare a CancellationToken parameter applies.
            await activeConnection.InvokeAsync(
                "StreamModelCommandProgress",
                modelCommandExecutor.ExecuteAsync(command, nodeId, commandCts.Token),
                commandCts.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not run model command {CommandId}", command.CommandId);
        }
    }

    /// <summary>
    /// A tool job arrives on the same outbound connection every other hub → node instruction uses
    /// (phase-26 D1). No inbound port appears on a GPU box because a node grew a second kind of
    /// engine.
    /// </summary>
    private async Task RunToolJobAsync(ToolJob job)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            logger.LogWarning("Received tool job {JobId} while not connected to coordinator", job.JobId);
            return;
        }

        Interlocked.Increment(ref inFlight);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

        try
        {
            activeJobs[job.JobId] = jobCts;

            // The capability and the model, never the payload and never an attachment: a tool
            // request may be a recording of somebody's voice (rule 7).
            logger.LogInformation(
                "Running {Capability} tool job {JobId} for model {Model}",
                job.Capability,
                job.JobId,
                job.Model);

            // Per-step progress goes up the same connection as everything else (phase 47, D2).
            // Fire-and-forget on purpose: a progress frame the hub never sees costs a client one
            // stale number, while awaiting it would put the coordinator's round trip inside the
            // worker's read loop and make a slow hub slow the GPU down.
            var progress = new Progress<ToolChunk>(chunk =>
            {
                if (activeConnection.State is not HubConnectionState.Connected)
                {
                    return;
                }

                _ = activeConnection.InvokeAsync("ToolJobProgress", chunk, CancellationToken.None)
                    .ContinueWith(
                        task => logger.LogDebug(task.Exception, "Could not report progress for tool job {JobId}", job.JobId),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
            });

            // Phase 53: when the job says its bytes are still coming, pull them down the same
            // connection. Null for every other job, so nothing about the pre-v3.21 path changes.
            var attachments = job.HasStreamedAttachments
                ? new Tools.HubAttachmentSource(activeConnection)
                : null;

            var result = await toolExecutor.RunAsync(job, progress, attachments, jobCts.Token);

            if (activeConnection.State is HubConnectionState.Connected)
            {
                // CancellationToken.None, not jobCts.Token: a cancelled job still has a result the
                // hub is waiting for, and sending it on the job's own token means every cancel is
                // followed by the hub timing out on a job the node already finished.
                await activeConnection.InvokeAsync("ToolJobResult", result, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not return result for tool job {JobId}", job.JobId);
        }
        finally
        {
            activeJobs.TryRemove(job.JobId, out _);
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task RunStreamingToolJobAsync(ToolJob job)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            logger.LogWarning("Received streaming tool job {JobId} while not connected", job.JobId);
            return;
        }

        Interlocked.Increment(ref inFlight);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

        try
        {
            activeJobs[job.JobId] = jobCts;
            logger.LogInformation(
                "Running streaming {Capability} tool job {JobId} for model {Model}",
                job.Capability,
                job.JobId,
                job.Model);

            // Uploaded as a client-to-server stream, exactly like StreamChunks — which is why the
            // hub method must not declare a CancellationToken parameter.
            await activeConnection.InvokeAsync(
                "StreamToolChunks",
                toolExecutor.StreamAsync(job, jobCts.Token),
                jobCts.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || jobCts.IsCancellationRequested)
        {
            logger.LogInformation("Streaming tool job {JobId} was canceled", job.JobId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not stream result for tool job {JobId}", job.JobId);
        }
        finally
        {
            activeJobs.TryRemove(job.JobId, out _);
            Interlocked.Decrement(ref inFlight);
        }
    }

    private void CancelJob(Guid jobId)
    {
        if (activeJobs.TryGetValue(jobId, out var jobCts))
        {
            logger.LogInformation("Canceling job {JobId} at coordinator request", jobId);
            jobCts.Cancel();
        }
    }

    private void OnAssignVectorReplica(VectorReplicaAssignment assignment)
    {
        try
        {
            replicaStore.Apply(assignment);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply replica assignment for '{Collection}'", assignment.Collection);
        }
    }

    private void OnApplyVectorOp(VectorReplicaOp op)
    {
        try
        {
            replicaStore.Apply(op);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply vector op for '{Collection}'", op.Collection);
        }
    }

    private void OnDropVectorReplica(string collection)
    {
        try
        {
            replicaStore.Drop(collection);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to drop replica '{Collection}'", collection);
        }
    }

    private async Task SendModelReportsAsync()
    {
        using var timer = new PeriodicTimer(coordinator.ModelRefreshInterval);

        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(lifetime.Token);
                await ReportModelsAsync(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Model refresh failed");
            }
        }
    }

    private async Task ReportModelsAsync(CancellationToken cancellationToken)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        var models = await backend.ListModelsAsync(cancellationToken);

        // Phase 69, v3.36.2, and the reason the release before it only half worked. **"Could not
        // ask" is not "has none."** Reporting an empty list here sends the coordinator a *failure*
        // dressed as a measurement, and it cannot be told from a box whose weights were deleted —
        // so the model left the registry and v3.36's `503` naming the backend reverted to the
        // `404 model not found` the phase exists to remove. Measured on the published image: the
        // 503 held for six seconds, and on defaults it would usually not appear at all, because a
        // refresh normally lands before the probe threshold is crossed.
        //
        // So the inventory is left exactly as the hub already has it, and the heartbeat — which
        // carries the health verdict within a probe or two — is what unroutes this node.
        if (models is null)
        {
            logger.LogWarning(
                "Could not list models from the {Backend} backend; leaving the coordinator's inventory alone rather than reporting zero. The heartbeat carries this backend's health.",
                backend.Name);

            return;
        }

        var filtered = ModelFilter.Apply(models, node.Models);

        if (activeConnection.State is not HubConnectionState.Connected)
        {
            return;
        }

        // Capabilities ride the model report rather than the registration (phase 40): this is
        // where the model list is refreshed, and at registration the node has not asked its
        // backend what it holds. Asking first would mean a node with a dead backend never
        // registers at all — the opposite of phase-36 D7, which exists so a broken box is
        // visible and unrouted rather than invisible.
        // Phase 41 folds the tool runtime's *live* list in here. A pool that gave up has emptied
        // its own, so the very next report unroutes this node for that capability — the phase-36
        // D7 mechanism reused rather than a health field invented.
        // Phase 43 narrows it once more, with what a coordinator profile switched off — which can
        // only ever be a superset of Node:Capabilities:Disabled, because the clamp that produced it
        // refuses to remove anything from that list.
        // Phase 67 adds the backend's own declaration to the inputs: an Anthropic-backed node says
        // chat and not embed, so the hub answers an embedding request with 40 D1's 503 rather than
        // routing it here for a 501.
        var capabilities = BackendCapabilities.Declare(
            filtered,
            backend.Kinds,
            node.Capabilities,
            toolRuntime.Capabilities,
            profiles.Effective.DisabledCapabilities);
        // Re-declared on every report as well as at registration (phase-53 D5): a hub that learned
        // it once would keep believing it after an operator turned the key off and restarted.
        var report = new NodeModels(
            nodeId,
            filtered,
            DateTimeOffset.UtcNow,
            capabilities,
            toolOptions.SupportsStreamedAttachments);
        await activeConnection.InvokeAsync("ReportModels", report, cancellationToken);

        // The empty report is the point, not an accident: the coordinator replaces this node's
        // list wholesale, so reporting nothing is what stops it routing inference at a backend
        // that cannot serve it. Preserving the last known good list instead would turn a
        // node-local fault into client-visible timeouts. What was missing was the *reason* —
        // "no models" read exactly like "this box has nothing installed".
        if (filtered.Count == 0 && supervisor.Health is { } health and not BackendHealth.Healthy)
        {
            logger.LogWarning(
                "Reported 0 models: the local backend is {Health}. This node stays unrouted until it recovers, and will re-report the moment it does.",
                health);

            return;
        }

        logger.LogInformation(
            "Reported {ModelCount} of {DiscoveredCount} models from {BackendName} backend as {Capabilities}",
            filtered.Count,
            models.Count,
            backend.Name,
            capabilities.Count == 0 ? "no capability" : string.Join(", ", capabilities.Select(c => c.Kind)));

        // Phase 44, D6. The corpus report rides the model loop rather than getting a timer of its
        // own: it is the same "what is this node doing now" refresh, and a second schedule is a
        // second thing to reason about when the two disagree.
        await ReportCorpusStateAsync(cancellationToken);

        // Phase 45, same loop for the same reason. The capability declaration above already says
        // *what* this node will serve; this says which manifest is behind it and which one is not
        // running, which is the difference between "nothing happened" and a reason.
        await ReportToolStateAsync(cancellationToken);
    }

    /// <summary>
    /// Tells the hub which tools this node loaded, started, suspended or gave up on (phase 45).
    /// <b>The hub never asks</b>, for phase-44 D6's reason — this is a mailbox, not a query.
    /// </summary>
    /// <remarks>
    /// A hub older than v3.13 has no such method, and that is a debug line and a node that carries
    /// on: phase-40 D1's mixed-fleet rule, for the fourth time.
    /// </remarks>
    private async Task ReportToolStateAsync(CancellationToken cancellationToken)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        try
        {
            await activeConnection.InvokeAsync("ReportToolState", toolRuntime.State(nodeId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "This coordinator did not accept a tool report");
        }
    }

    /// <summary>
    /// Tells the hub what this node's corpus is doing (phase 44, D6). <b>The hub never asks</b> — it
    /// would be a synchronous dependency on a box that may be asleep, and <c>/api/status</c> has to
    /// answer when the fleet does not.
    /// </summary>
    /// <remarks>
    /// A hub older than v3.12 has no such method, and that is a debug line and a node that carries on
    /// (phase-40 D1's mixed-fleet rule, for the third time).
    /// </remarks>
    private async Task ReportCorpusStateAsync(CancellationToken cancellationToken)
    {
        var activeConnection = connection;

        if (activeConnection is not { State: HubConnectionState.Connected })
        {
            return;
        }

        NodeCorpusState state;

        try
        {
            state = await retrieval.StateAsync(nodeId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the corpus state to report it");
            return;
        }

        try
        {
            await activeConnection.InvokeAsync("ReportCorpusState", state, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "This coordinator did not accept a corpus report");
        }
    }

    private static Uri BuildHubUrl(string coordinatorUrl)
    {
        var baseUri = new Uri(coordinatorUrl, UriKind.Absolute);
        return new Uri(baseUri, "/hubs/node");
    }

    private static string GetVersion()
    {
        return typeof(CoordinatorConnection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(CoordinatorConnection).Assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}
