using System.Reflection;
using System.Collections.Concurrent;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
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
    ReplicaStore replicaStore,
    ILogger<CoordinatorConnection> logger) : IAsyncDisposable
{
    private readonly CoordinatorOptions coordinator = coordinatorOptions.Value;
    private readonly NodeOptions node = nodeOptions.Value;

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

    public Task StartAsync(CancellationToken cancellationToken)
        => ConnectUntilSuccessfulAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
        await lifetime.CancelAsync();
        reconnectLock.Dispose();
        lifetime.Dispose();

        if (connection is not null)
        {
            await connection.DisposeAsync();
        }
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
            node.MaxConcurrency,
            inventory.Count == 0 ? null : inventory,
            backend.SupportsModelManagement);

        await connection.InvokeAsync("Register", registration, cancellationToken);
        await ReportModelsAsync(cancellationToken);

        logger.LogInformation(
            "Registered node {NodeId} ({NodeName}) with coordinator",
            registration.NodeId,
            registration.Name);
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

        var heartbeat = new Heartbeat(nodeId, DateTimeOffset.UtcNow, Volatile.Read(ref inFlight));
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
        var filtered = ModelFilter.Apply(models, node.Models);

        if (activeConnection.State is not HubConnectionState.Connected)
        {
            return;
        }

        var report = new NodeModels(nodeId, filtered, DateTimeOffset.UtcNow);
        await activeConnection.InvokeAsync("ReportModels", report, cancellationToken);

        logger.LogInformation(
            "Reported {ModelCount} of {DiscoveredCount} models from {BackendName} backend",
            filtered.Count,
            models.Count,
            backend.Name);
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
