using System.Reflection;
using System.Collections.Concurrent;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace InferHub.Node;

public sealed class CoordinatorConnection(
    IOptions<CoordinatorOptions> coordinatorOptions,
    IOptions<NodeOptions> nodeOptions,
    IOptions<OllamaOptions> ollamaOptions,
    INodeIdentity nodeIdentity,
    IInferenceBackend backend,
    InferenceExecutor inferenceExecutor,
    ILogger<CoordinatorConnection> logger) : IAsyncDisposable
{
    private readonly CoordinatorOptions coordinator = coordinatorOptions.Value;
    private readonly NodeOptions node = nodeOptions.Value;
    private readonly OllamaOptions ollama = ollamaOptions.Value;

    private readonly SemaphoreSlim reconnectLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string nodeId = nodeIdentity.GetOrCreateNodeId();
    private HubConnection? connection;
    private Task? heartbeatTask;
    private Task? modelRefreshTask;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeJobs = new();
    private int inFlight;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        connection = BuildConnection();
        RegisterConnectionHandlers(connection);

        await ConnectUntilSuccessfulAsync(cancellationToken);
    }

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

    private HubConnection BuildConnection()
    {
        var hubUrl = BuildHubUrl(coordinator.Url);
        var enrollmentSecret = coordinator.EnrollmentSecret;

        if (string.IsNullOrWhiteSpace(enrollmentSecret))
        {
            logger.LogWarning(
                "Coordinator:EnrollmentSecret is not configured; the coordinator will refuse this node.");
        }

        return new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                if (!string.IsNullOrWhiteSpace(enrollmentSecret))
                {
                    options.Headers["X-Node-Enrollment-Secret"] = enrollmentSecret;
                }
            })
            .WithAutomaticReconnect()
            .Build();
    }

    private void RegisterConnectionHandlers(HubConnection hubConnection)
    {
        hubConnection.On<InferenceJob>("RunJob", RunJobAsync);
        hubConnection.On<InferenceJob>("RunStreamingJob", RunStreamingJobAsync);
        hubConnection.On<Guid>("CancelJob", CancelJob);

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

            logger.LogWarning(error, "Coordinator connection closed; retrying");
            await ConnectUntilSuccessfulAsync(lifetime.Token);
        };
    }

    private async Task ConnectUntilSuccessfulAsync(CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new InvalidOperationException("Coordinator connection has not been built.");
        }

        await reconnectLock.WaitAsync(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (connection.State is HubConnectionState.Connected)
                {
                    return;
                }

                try
                {
                    logger.LogInformation("Connecting to coordinator");
                    await connection.StartAsync(cancellationToken);
                    await RegisterAsync(cancellationToken);
                    EnsureHeartbeatLoop();
                    EnsureModelRefreshLoop();
                    logger.LogInformation("Connected to coordinator");
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
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

    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new InvalidOperationException("Coordinator connection has not been built.");
        }

        var registration = new NodeRegistration(
            nodeId,
            node.Name,
            ollama.Endpoint,
            GetVersion(),
            node.Labels.Count == 0 ? null : new Dictionary<string, string>(node.Labels),
            node.MaxConcurrency);

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

    private void CancelJob(Guid jobId)
    {
        if (activeJobs.TryGetValue(jobId, out var jobCts))
        {
            logger.LogInformation("Canceling job {JobId} at coordinator request", jobId);
            jobCts.Cancel();
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
