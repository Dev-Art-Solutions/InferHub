using System.Collections.Concurrent;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// The async image-job surface on a standalone node (phase 47; phase-41 D8).
/// </summary>
/// <remarks>
/// <para>
/// Phase-37 D2's framing for the fourth time: the hub's version is this, plus routing. Here there is
/// nothing to route to, so the queue is one line and the resource it is fair about is this box's
/// own worker pool. What is <em>not</em> duplicated is the part that decides what a job is and when
/// its bytes go away — <see cref="ImageJobStore"/> is the same class the coordinator runs, so "does
/// a solo node keep an image longer than a hub does" is a question that cannot have two answers.
/// </para>
/// <para>
/// Concurrency is deliberately <b>one job at a time</b>. A standalone diffusion node is a box with a
/// card in it, and letting two jobs into a pool sized <c>maxWorkers: 1</c> would only move the queue
/// one layer down, where it has no position to report and answers a 503 instead.
/// </para>
/// </remarks>
public sealed class LocalImageJobRunner(
    IOptions<ImageEdgeOptions> options,
    ToolExecutor executor,
    ILogger<LocalImageJobRunner> logger)
{
    private readonly ConcurrentDictionary<Guid, PendingLocalJob> pending = new();
    private readonly SemaphoreSlim pump = new(1, 1);
    private int running;

    public ImageJobStore Store { get; } = new(options.Value.Jobs);

    public ImageJobRecord? TrySubmit(string clientId, IImageRequest request)
    {
        var id = Guid.NewGuid();

        if (!Store.TryCreate(id, clientId, request.Model, request.Count, out var record))
        {
            return null;
        }

        pending[id] = new PendingLocalJob(request, new CancellationTokenSource());

        // The capability, the model and the count. Never the prompt: a prompt is what somebody
        // wanted, and on a solo box there is no hub between them and this log file.
        logger.LogInformation(
            "Accepted {Capability} job {JobId} ({Model}, n={Count})",
            request.Capability,
            id,
            request.Model,
            request.Count);

        Pump();
        return record;
    }

    public bool Cancel(Guid id, string clientId)
    {
        var record = Store.Find(id, clientId);

        if (record is null || record.IsTerminal)
        {
            return false;
        }

        if (record.State == ImageJobStates.Queued)
        {
            Finish(id, ImageJobStates.Cancelled, ImageJobReasons.Cancelled, "the job was cancelled before it started");
            Pump();
            return true;
        }

        Store.TryTransition(id, ImageJobStates.Cancelling, ImageJobReasons.Cancelled);

        if (pending.TryGetValue(id, out var job))
        {
            job.Cancellation.Cancel();
        }

        return true;
    }

    public void Sweep()
    {
        foreach (var record in Store.Sweep())
        {
            if (pending.TryRemove(record.Id, out var job))
            {
                job.Dispose();
            }
        }
    }

    private void Pump() => _ = Task.Run(PumpAsync);

    private async Task PumpAsync()
    {
        if (!await pump.WaitAsync(TimeSpan.Zero))
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref running) == 1)
            {
                return;
            }

            var next = Store.Queued().FirstOrDefault();

            if (next is null || !pending.TryGetValue(next.Id, out var job))
            {
                return;
            }

            if (!Store.TryTransition(next.Id, ImageJobStates.Running))
            {
                return;
            }

            Volatile.Write(ref running, 1);
            _ = RunAsync(next.Id, job);
        }
        finally
        {
            pump.Release();
        }
    }

    private async Task RunAsync(Guid id, PendingLocalJob job)
    {
        var toolJob = new ToolJob(
            id,
            job.Request.Capability,
            job.Request.Model,
            job.Request.ToToolPayload(),
            job.Request.Attachments.Count == 0 ? null : job.Request.Attachments);

        var progress = new Progress<ToolChunk>(chunk =>
        {
            if (ImageProgress.TryRead(chunk.Payload, out var step, out var totalSteps))
            {
                Store.ReportProgress(id, step, totalSteps);
            }
        });

        try
        {
            var result = await executor.RunAsync(toolJob, progress, job.Cancellation.Token);
            var outcome = ImageRenderer.Render(result, job.Request, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            logger.LogInformation(
                "{Capability} job {JobId} ({Model}): {Status}, {Images} image(s), {Units:F1} megapixel-steps",
                job.Request.Capability,
                id,
                job.Request.Model,
                outcome.Status,
                outcome.ImageCount,
                outcome.Units);

            if (!result.Success
                && string.Equals(result.ErrorCode, ToolErrorCodes.Cancelled, StringComparison.Ordinal))
            {
                Finish(id, ImageJobStates.Cancelled, ImageJobReasons.Cancelled, result.Error);
                return;
            }

            if (outcome.IsError)
            {
                // The renderer's decision travels with the job, so a job-watching client sees the
                // same `errorCode` a synchronous caller's status was derived from.
                Finish(
                    id,
                    ImageJobStates.Failed,
                    ImageJobReasons.WorkerError,
                    outcome.Error,
                    new ImageJobFailure(
                        outcome.Status,
                        outcome.Error,
                        outcome.ErrorType,
                        outcome.ErrorCode,
                        outcome.ErrorParam,
                        outcome.RetryAfterSeconds));

                return;
            }

            Store.TrySucceed(id, outcome.Images, outcome.Units, outcome.Summary);
            Forget(id);
        }
        catch (OperationCanceledException)
        {
            Finish(id, ImageJobStates.Cancelled, ImageJobReasons.Cancelled, "the job was cancelled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image job {JobId} failed", id);
            Finish(id, ImageJobStates.Failed, ImageJobReasons.WorkerError, ex.Message);
        }
        finally
        {
            Volatile.Write(ref running, 0);
            Pump();
        }
    }

    private void Finish(Guid id, string state, string reason, string? error, ImageJobFailure? failure = null)
    {
        Store.TryTransition(id, state, reason, error, failure: failure);
        Forget(id);
    }

    private void Forget(Guid id)
    {
        if (Store.Peek(id) is { IsTerminal: true } && pending.TryRemove(id, out var job))
        {
            job.Dispose();
        }
    }

    private sealed record PendingLocalJob(IImageRequest Request, CancellationTokenSource Cancellation)
        : IDisposable
    {
        public void Dispose() => Cancellation.Dispose();
    }
}

/// <summary>Retention on a solo node, on the same five-second tick the hub uses.</summary>
public sealed class LocalImageJobSweeper(LocalImageJobRunner jobs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            jobs.Sweep();
        }
    }
}
