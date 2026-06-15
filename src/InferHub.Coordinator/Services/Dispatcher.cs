using System.Collections.Concurrent;
using System.Threading.Channels;
using InferHub.Coordinator.Hubs;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class Dispatcher(
    IHubContext<NodeHub> hubContext,
    IOptions<DispatcherOptions> options,
    ILogger<Dispatcher> logger) : IDispatcher
{
    private readonly ConcurrentDictionary<Guid, PendingResult> pendingResults = new();
    private readonly ConcurrentDictionary<Guid, PendingStream> pendingStreams = new();

    public async Task<InferenceResult> DispatchAsync(
        RoutableNode node,
        InferenceJob job,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<InferenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingResult(node.ConnectionId, tcs);

        if (!pendingResults.TryAdd(job.JobId, pending))
        {
            throw new InvalidOperationException($"Job '{job.JobId}' is already pending.");
        }

        using var registration = cancellationToken.Register(
            static state =>
            {
                var (dispatcher, connectionId, jobId) = ((Dispatcher, string, Guid))state!;
                dispatcher.SendCancelJob(connectionId, jobId);
            },
            (this, node.ConnectionId, job.JobId));

        try
        {
            logger.LogInformation(
                "Dispatching {JobKind} job {JobId} to node {NodeId} ({NodeName})",
                job.Kind,
                job.JobId,
                node.NodeId,
                node.Name);

            await hubContext.Clients.Client(node.ConnectionId).SendAsync("RunJob", job, cancellationToken);

            var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));
            return await tcs.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            SendCancelJob(node.ConnectionId, job.JobId);
            throw;
        }
        finally
        {
            pendingResults.TryRemove(job.JobId, out _);
        }
    }

    public async Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(
        RoutableNode node,
        InferenceJob job,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<InferenceChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var pending = new PendingStream(node.ConnectionId, channel);
        pending.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var (dispatcher, connectionId, jobId) = ((Dispatcher, string, Guid))state!;
                dispatcher.CancelStream(connectionId, jobId, new OperationCanceledException("inference request was canceled"));
            },
            (this, node.ConnectionId, job.JobId));

        if (!pendingStreams.TryAdd(job.JobId, pending))
        {
            pending.CancellationRegistration.Dispose();
            throw new InvalidOperationException($"Job '{job.JobId}' is already pending.");
        }

        try
        {
            logger.LogInformation(
                "Dispatching streaming {JobKind} job {JobId} to node {NodeId} ({NodeName})",
                job.Kind,
                job.JobId,
                node.NodeId,
                node.Name);

            WatchStreamTimeout(job.JobId, node.ConnectionId);
            await hubContext.Clients.Client(node.ConnectionId).SendAsync("RunStreamingJob", job, cancellationToken);
            return channel.Reader;
        }
        catch
        {
            if (pendingStreams.TryRemove(job.JobId, out var removed))
            {
                removed.CancellationRegistration.Dispose();
                removed.Channel.Writer.TryComplete();
            }

            throw;
        }
    }

    public bool Complete(InferenceResult result)
    {
        if (!pendingResults.TryRemove(result.JobId, out var pending))
        {
            logger.LogWarning("Received result for unknown or expired job {JobId}", result.JobId);
            return false;
        }

        logger.LogInformation("Completed job {JobId}; success={Success}", result.JobId, result.Success);
        return pending.Completion.TrySetResult(result);
    }

    public bool WriteChunk(InferenceChunk chunk)
    {
        if (!pendingStreams.TryGetValue(chunk.JobId, out var pending))
        {
            logger.LogWarning("Received chunk for unknown or expired job {JobId}", chunk.JobId);
            return false;
        }

        var written = pending.Channel.Writer.TryWrite(chunk);

        if (chunk.Done && pendingStreams.TryRemove(chunk.JobId, out var completed))
        {
            completed.CancellationRegistration.Dispose();
            completed.Channel.Writer.TryComplete();
            logger.LogInformation("Completed streaming job {JobId}", chunk.JobId);
        }

        return written;
    }

    public void FailForConnection(string connectionId, Exception? exception)
    {
        foreach (var (jobId, pending) in pendingResults)
        {
            if (pending.ConnectionId == connectionId && pendingResults.TryRemove(jobId, out var removed))
            {
                removed.Completion.TrySetException(
                    exception ?? new InvalidOperationException("node disconnected before returning a result"));
            }
        }

        foreach (var (jobId, pending) in pendingStreams)
        {
            if (pending.ConnectionId == connectionId && pendingStreams.TryRemove(jobId, out var removed))
            {
                removed.CancellationRegistration.Dispose();
                removed.Channel.Writer.TryComplete(
                    exception ?? new InvalidOperationException("node disconnected before finishing the stream"));
            }
        }
    }

    private void WatchStreamTimeout(Guid jobId, string connectionId)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.TimeoutSeconds));

        _ = Task.Run(async () =>
        {
            await Task.Delay(timeout);

            if (pendingStreams.TryRemove(jobId, out var pending))
            {
                pending.CancellationRegistration.Dispose();
                pending.Channel.Writer.TryComplete(new TimeoutException("inference request timed out"));
                SendCancelJob(connectionId, jobId);
            }
        });
    }

    private void CancelStream(string connectionId, Guid jobId, Exception exception)
    {
        if (pendingStreams.TryRemove(jobId, out var pending))
        {
            pending.CancellationRegistration.Dispose();
            pending.Channel.Writer.TryComplete(exception);
            SendCancelJob(connectionId, jobId);
        }
    }

    private void SendCancelJob(string connectionId, Guid jobId)
    {
        _ = hubContext.Clients.Client(connectionId)
            .SendAsync("CancelJob", jobId)
            .ContinueWith(
                task => logger.LogWarning(task.Exception, "Could not send cancellation for job {JobId}", jobId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    private sealed record PendingResult(
        string ConnectionId,
        TaskCompletionSource<InferenceResult> Completion);

    private sealed class PendingStream(string connectionId, Channel<InferenceChunk> channel)
    {
        public string ConnectionId { get; } = connectionId;

        public Channel<InferenceChunk> Channel { get; } = channel;

        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}
