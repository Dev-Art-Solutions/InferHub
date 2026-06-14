using System.Reflection;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace InferHub.Node;

public sealed class CoordinatorConnection(
    IConfiguration configuration,
    INodeIdentity nodeIdentity,
    IInferenceBackend backend,
    InferenceExecutor inferenceExecutor,
    ILogger<CoordinatorConnection> logger) : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ModelRefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim reconnectLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string nodeId = nodeIdentity.GetOrCreateNodeId();
    private HubConnection? connection;
    private Task? heartbeatTask;
    private Task? modelRefreshTask;
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
        var hubUrl = BuildHubUrl(configuration["Coordinator:Url"] ?? "http://localhost:5080/");

        return new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    private void RegisterConnectionHandlers(HubConnection hubConnection)
    {
        hubConnection.On<InferenceJob>("RunJob", RunJobAsync);

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
                    logger.LogWarning(ex, "Could not connect to coordinator; retrying in {DelaySeconds}s", RetryDelay.TotalSeconds);
                    await Task.Delay(RetryDelay, cancellationToken);
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
            configuration["Node:Name"] ?? Environment.MachineName,
            configuration["Ollama:Endpoint"] ?? "http://localhost:11434/",
            GetVersion());

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
        using var timer = new PeriodicTimer(HeartbeatInterval);

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

        try
        {
            logger.LogInformation("Running {JobKind} job {JobId}", job.Kind, job.JobId);
            var result = await inferenceExecutor.RunAsync(job, lifetime.Token);

            if (activeConnection.State is HubConnectionState.Connected)
            {
                await activeConnection.InvokeAsync("JobResult", result, lifetime.Token);
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
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task SendModelReportsAsync()
    {
        using var timer = new PeriodicTimer(ModelRefreshInterval);

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

        if (activeConnection.State is not HubConnectionState.Connected)
        {
            return;
        }

        var report = new NodeModels(nodeId, models, DateTimeOffset.UtcNow);
        await activeConnection.InvokeAsync("ReportModels", report, cancellationToken);

        logger.LogInformation(
            "Reported {ModelCount} models from {BackendName} backend",
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
