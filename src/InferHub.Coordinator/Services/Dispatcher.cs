using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<InferenceResult>> pending = new();

    public async Task<InferenceResult> DispatchAsync(
        RoutableNode node,
        InferenceJob job,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<InferenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!pending.TryAdd(job.JobId, tcs))
        {
            throw new InvalidOperationException($"Job '{job.JobId}' is already pending.");
        }

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
        finally
        {
            pending.TryRemove(job.JobId, out _);
        }
    }

    public bool Complete(InferenceResult result)
    {
        if (!pending.TryRemove(result.JobId, out var tcs))
        {
            logger.LogWarning("Received result for unknown or expired job {JobId}", result.JobId);
            return false;
        }

        logger.LogInformation("Completed job {JobId}; success={Success}", result.JobId, result.Success);
        return tcs.TrySetResult(result);
    }
}
