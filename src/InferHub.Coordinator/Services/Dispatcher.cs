using System.Collections.Concurrent;
using System.Threading.Channels;
using InferHub.Coordinator.Hubs;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

public sealed class Dispatcher(
    IHubContext<NodeHub> hubContext,
    INodeRegistry registry,
    Metrics metrics,
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
        var pending = new PendingResult(node.ConnectionId, node.NodeId, tcs);

        if (!pendingResults.TryAdd(job.JobId, pending))
        {
            throw new InvalidOperationException($"Job '{job.JobId}' is already pending.");
        }

        registry.IncrementInFlight(node.ConnectionId);
        metrics.RecordRequestStart(node.NodeId);

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
            if (pendingResults.TryRemove(job.JobId, out _))
            {
                registry.DecrementInFlight(node.ConnectionId);
            }
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
        var pending = new PendingStream(node.ConnectionId, node.NodeId, channel);
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

        registry.IncrementInFlight(node.ConnectionId);
        metrics.RecordRequestStart(node.NodeId);

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

            // Wait until either the first chunk arrives (stream is "live") or we get a
            // pre-stream error (node dropped, timeout, cancellation). This lets the endpoint
            // retry on another node if nothing was emitted yet, while a started stream
            // surfaces its error through the channel reader.
            await pending.StreamReady.Task.WaitAsync(cancellationToken);

            return channel.Reader;
        }
        catch
        {
            if (pendingStreams.TryRemove(job.JobId, out var removed))
            {
                registry.DecrementInFlight(removed.ConnectionId);
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

        registry.DecrementInFlight(pending.ConnectionId);

        if (result.Success)
        {
            metrics.RecordRequestComplete(pending.NodeId);
        }
        else
        {
            metrics.RecordRequestFail(pending.NodeId);
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

        // Mark the stream as "started" only after we've successfully buffered a chunk.
        // Pre-stream failover hinges on this — once it flips, FailForConnection no longer
        // throws NodeDisconnectedException, it ends the stream with an error chunk.
        pending.MarkStarted();

        if (chunk.Done && pendingStreams.TryRemove(chunk.JobId, out var completed))
        {
            registry.DecrementInFlight(completed.ConnectionId);
            metrics.RecordRequestComplete(completed.NodeId);
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
                registry.DecrementInFlight(removed.ConnectionId);
                metrics.RecordRequestFail(removed.NodeId);

                var error = new NodeDisconnectedException(
                    removed.ConnectionId,
                    "node disconnected before returning a result",
                    exception);

                removed.Completion.TrySetException(error);
            }
        }

        foreach (var (jobId, pending) in pendingStreams)
        {
            if (pending.ConnectionId == connectionId && pendingStreams.TryRemove(jobId, out var removed))
            {
                registry.DecrementInFlight(removed.ConnectionId);
                metrics.RecordRequestFail(removed.NodeId);
                removed.CancellationRegistration.Dispose();

                var preStream = !removed.Started;
                var error = new NodeDisconnectedException(
                    removed.ConnectionId,
                    preStream
                        ? "node disconnected before the stream started"
                        : "node disconnected before finishing the stream",
                    exception);

                if (preStream)
                {
                    // Surface as an exception so DispatchStreamAsync can throw and the
                    // endpoint retries on another capable node.
                    removed.StreamReady.TrySetException(error);
                    removed.Channel.Writer.TryComplete(error);
                }
                else
                {
                    // The client already started receiving chunks — end the stream
                    // cleanly with an error so they don't hang waiting for the rest.
                    removed.Channel.Writer.TryComplete(error);
                }
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
                registry.DecrementInFlight(pending.ConnectionId);
                metrics.RecordRequestFail(pending.NodeId);
                pending.CancellationRegistration.Dispose();

                var error = new TimeoutException("inference request timed out");
                pending.StreamReady.TrySetException(error);
                pending.Channel.Writer.TryComplete(error);
                SendCancelJob(connectionId, jobId);
            }
        });
    }

    private void CancelStream(string connectionId, Guid jobId, Exception exception)
    {
        if (pendingStreams.TryRemove(jobId, out var pending))
        {
            registry.DecrementInFlight(pending.ConnectionId);
            metrics.RecordRequestFail(pending.NodeId);
            pending.CancellationRegistration.Dispose();
            pending.StreamReady.TrySetException(exception);
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
        string NodeId,
        TaskCompletionSource<InferenceResult> Completion);

    private sealed class PendingStream(string connectionId, string nodeId, Channel<InferenceChunk> channel)
    {
        private int started;

        public string ConnectionId { get; } = connectionId;

        public string NodeId { get; } = nodeId;

        public Channel<InferenceChunk> Channel { get; } = channel;

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public TaskCompletionSource<bool> StreamReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Started => Volatile.Read(ref started) != 0;

        public void MarkStarted()
        {
            if (Interlocked.Exchange(ref started, 1) == 0)
            {
                StreamReady.TrySetResult(true);
            }
        }
    }
}
