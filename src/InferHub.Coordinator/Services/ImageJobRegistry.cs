using System.Collections.Concurrent;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The hub's async image-job surface (phase 47): a bounded FIFO queue, one dispatch per capable
/// node, per-step progress, cooperative cancel, and results that live in memory for five minutes.
/// </summary>
/// <remarks>
/// <para>
/// The <em>store</em> — what a job is, when its bytes go away — is
/// <see cref="ImageJobStore"/> in <c>InferHub.Shared</c>, because solo mode serves the same five
/// routes and two answers to "when do the bytes go away" is one answer too many. What lives here is
/// everything that needs a fleet: routing, dispatch, metering and the ledger.
/// </para>
/// <para>
/// <b>The queue is FIFO and it is not clever</b> (D5). Shortest-job-first would let a stream of
/// four-step requests starve a fifty-step one indefinitely, and the starvation would be invisible;
/// fair-share-by-client needs a notion of tenant weight phase 25's client model does not have and
/// this phase is not the place to invent one. A GPU running diffusion is a resource there is exactly
/// one of, so the pump gives each capable node one job at a time and takes the queue in order.
/// </para>
/// <para>
/// <b>A running job is never retried</b> (D7). Phase-21 D3's pre-stream failover retries a request
/// that has not produced output, and an image job that died at step 22 has produced none — so it is
/// <em>technically</em> retryable, and retrying it would silently double the GPU-minutes and the
/// ledger units for one request. It fails with <c>node_lost</c> and the client decides. A job still
/// <c>queued</c> when its node disappears has spent nothing and is simply routed again on the next
/// pass.
/// </para>
/// </remarks>
public sealed class ImageJobRegistry
{
    private readonly ImageJobStore store;
    private readonly IRouter router;
    private readonly INodeRegistry nodes;
    private readonly IToolDispatcher tools;
    private readonly UsageMeter usage;
    private readonly Metrics metrics;
    private readonly ILogger<ImageJobRegistry> logger;

    private readonly ConcurrentDictionary<Guid, PendingImageJob> pending = new();

    /// <summary>Node ids currently running an image job. The "exactly one of" the queue is fair about.</summary>
    private readonly ConcurrentDictionary<string, Guid> busyNodes = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim pump = new(1, 1);

    public ImageJobRegistry(
        IOptions<ImageEdgeOptions> options,
        IRouter router,
        INodeRegistry nodes,
        IToolDispatcher tools,
        UsageMeter usage,
        Metrics metrics,
        IImageJobArchive archive,
        ILogger<ImageJobRegistry> logger)
    {
        // Phase 56. The archive is `NoImageJobArchive` unless Images:Jobs:Persistence=file, in which
        // case constructing the store is also what reads the last run's jobs back — see
        // ImageJobStore.Restore for why the window is applied there rather than by the sweeper.
        store = new ImageJobStore(options.Value.Jobs, archive: archive);
        this.router = router;
        this.nodes = nodes;
        this.tools = tools;
        this.usage = usage;
        this.metrics = metrics;
        this.logger = logger;
    }

    public ImageJobStore Store => store;

    /// <summary>
    /// Accepts a job, or refuses because the queue is full. The refusal is a <c>503</c> +
    /// <c>Retry-After</c> at the edge — the same status and header as every other limit here.
    /// </summary>
    public ImageJobRecord? TrySubmit(
        ResolvedClient client,
        IImageRequest request,
        IDisposable? admissionLease)
    {
        var id = Guid.NewGuid();

        if (!store.TryCreate(id, client.Id, request, out var record))
        {
            admissionLease?.Dispose();
            return null;
        }

        pending[id] = new PendingImageJob(client, request, admissionLease, new CancellationTokenSource());

        // The capability, the model, the count and the client — never the prompt, and never
        // anything about an uploaded picture. A prompt is content in exactly the sense rule 7
        // means: it is what somebody wanted, and the picture is the answer.
        logger.LogInformation(
            "Accepted {Capability} job {JobId} ({Model}, n={Count}) for client {ClientId}",
            request.Capability,
            id,
            request.Model,
            request.Count,
            client.Id);

        Pump();
        return record;
    }

    public ImageJobRecord? Find(Guid id, string clientId) => store.Find(id, clientId);

    public int? QueuePosition(Guid id) => store.QueuePosition(id);

    public ImageJobImage? TryTakeContent(Guid id, string clientId, int index) =>
        store.TryTakeContent(id, clientId, index);

    /// <summary>
    /// Asks for a cancel. Best effort, and the API says so: a job cancelled at step 27 of 28 may
    /// still succeed, and a client that gets <c>succeeded</c> after asking gets its image.
    /// </summary>
    public bool Cancel(Guid id, string clientId)
    {
        var record = store.Find(id, clientId);

        if (record is null || record.IsTerminal)
        {
            return false;
        }

        if (record.State == ImageJobStates.Queued)
        {
            // Nothing was ever spent, so there is nothing to be best-effort about.
            Finish(id, ImageJobStates.Cancelled, ImageJobReasons.Cancelled, "the job was cancelled before it started");
            Pump();
            return true;
        }

        store.TryTransition(id, ImageJobStates.Cancelling, ImageJobReasons.Cancelled);

        if (pending.TryGetValue(id, out var job))
        {
            // This reaches the node as `CancelJob` on the connection it already opened, and the
            // node turns it into a cooperative `cancel` frame rather than a kill (D3).
            job.Cancellation.Cancel();
        }

        return true;
    }

    /// <remarks>
    /// A node going away needs no hook here: <c>NodeHub.OnDisconnectedAsync</c> already calls
    /// <c>Dispatcher.FailForConnection</c>, which faults the pending dispatch with a
    /// <see cref="NodeDisconnectedException"/> — and that lands in <see cref="RunAsync"/>, where the
    /// job is failed with <c>node_lost</c> and, deliberately, not retried. One path, and it is the
    /// path a real disconnect takes.
    /// </remarks>
    private static string NodeLostMessage(string nodeId) =>
        $"node '{nodeId}' disappeared while this job was running; it was not retried, because a retry " +
        "would spend the GPU minutes and the ledger units twice for one request";

    /// <summary>Retention and the byte ceiling, driven by <see cref="ImageJobSweeper"/>.</summary>
    public void Sweep()
    {
        foreach (var record in store.Sweep())
        {
            pending.TryRemove(record.Id, out var job);
            job?.Dispose();
        }
    }

    // ---- the pump ------------------------------------------------------------------------------

    /// <summary>
    /// Takes the queue in order and starts what it can. Called on every event that could change the
    /// answer — a submission, a completion, a cancel, a node loss — rather than on a timer, so a
    /// freed GPU is used in milliseconds instead of at the next tick.
    /// </summary>
    private void Pump() => _ = Task.Run(PumpAsync);

    private async Task PumpAsync()
    {
        if (!await pump.WaitAsync(TimeSpan.Zero))
        {
            // Somebody is already pumping. They will see whatever we were going to see, because
            // they re-read the queue after every start.
            return;
        }

        try
        {
            foreach (var record in store.Queued())
            {
                if (!pending.TryGetValue(record.Id, out var job))
                {
                    continue;
                }

                var node = Route(record.Model, job.Request.Capability);

                if (node is null)
                {
                    // Head-of-line, deliberately: a job behind this one may be routable, and
                    // stepping over it would be shortest-job-first wearing a scheduler's clothes.
                    break;
                }

                if (!store.TryTransition(record.Id, ImageJobStates.Running, nodeId: node.NodeId))
                {
                    continue;
                }

                busyNodes[node.NodeId] = record.Id;
                _ = RunAsync(record.Id, node, job);
            }
        }
        finally
        {
            pump.Release();
        }
    }

    /// <summary>
    /// A node that declares this capability for this model and is not already running an image job.
    /// Nothing here reaches into the router's own ranking — it asks for a candidate and then applies
    /// the one extra rule this phase owns.
    /// </summary>
    /// <param name="capability">
    /// <c>image</c> or <c>image-edit</c> (phase 50). <b><see cref="busyNodes"/> is deliberately not
    /// split by it</b>: the resource a node has exactly one of is the card, and letting an edit and
    /// a generation start together on one box would be two pipelines' worth of VRAM for a fairness
    /// rule that exists precisely to stop that.
    /// </param>
    private RoutableNode? Route(string model, string capability)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var node = router.Route(
                model,
                excludeConnectionId: excluded.Count == 1 ? excluded.First() : null,
                capability: capability);

            if (node is null)
            {
                return null;
            }

            if (!busyNodes.ContainsKey(node.NodeId))
            {
                return node;
            }

            if (!excluded.Add(node.ConnectionId) || excluded.Count > 1)
            {
                // The router takes one exclusion, so past the second distinct candidate there is
                // nothing more to ask it. Everything it would offer is busy; wait for a completion.
                return null;
            }
        }
    }

    private async Task RunAsync(Guid id, RoutableNode node, PendingImageJob job)
    {
        var toolJob = new ToolJob(
            id,
            job.Request.Capability,
            job.Request.Model,
            job.Request.ToToolPayload(),

            // An edit's input picture and mask, travelling the phase-40 D4 attachment path in the
            // direction it had never been used in: hub → node. Empty for a generation, so nothing
            // about that dispatch changed.
            job.Request.Attachments.Count == 0 ? null : job.Request.Attachments);

        var progress = new Progress<ToolChunk>(chunk =>
        {
            if (ImageProgress.TryRead(chunk.Payload, out var step, out var totalSteps))
            {
                store.ReportProgress(id, step, totalSteps);
            }
        });

        try
        {
            var result = await tools.DispatchToolAsync(node, toolJob, progress, job.Cancellation.Token);

            // One dispatch path, two renderings (phase-57 D10). The queue, the progress, the cancel,
            // the retention and the archive are the same for a clip as for a picture; what differs is
            // the envelope a client reads and the second unit a video meters, and both of those live
            // in a renderer rather than in a second registry.
            var outcome = job.Request is VideoGenerationRequest video
                ? VideoRenderer.Render(result, video)
                : ImageRenderer.Render(result, job.Request, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            logger.LogInformation(
                "{Capability} job {JobId} ({Model}) on node {NodeId}: {Status}, {Images} image(s), {Units:F1} megapixel-steps",
                job.Request.Capability,
                id,
                job.Request.Model,
                node.NodeId,
                outcome.Status,
                outcome.ImageCount,
                outcome.Units);

            if (IsCancellation(result))
            {
                Finish(id, ImageJobStates.Cancelled, ImageJobReasons.Cancelled, result.Error);
                return;
            }

            if (outcome.IsError)
            {
                // The renderer's decision travels with the job, so the synchronous route answers
                // the 3.14 status — a worker's `invalid_request` is still a 400 and a busy tool is
                // still a 503 with its Retry-After.
                Finish(id, ImageJobStates.Failed, ImageJobReasons.WorkerError, outcome.Error, Failure(outcome));
                return;
            }

            if (store.TrySucceed(id, outcome.Images, outcome.Units, outcome.Summary))
            {
                Count(id, ImageJobStates.Succeeded);

                // Metered exactly where phase 46 meters it — the one place that already decides a
                // job succeeded — so the number on a dashboard and the number on a bill cannot come
                // from two definitions of "done".
                // Metered under the capability that ran it, so an operator can see what the card
                // actually spent its minutes on. The *unit* is the same either way — megapixel-steps
                // is a fact about pixels and steps, and an edit's steps are the ones it really ran
                // (phase-50 D3), not the ones that were asked for.
                usage.RecordUnits(job.Client, job.Request.Capability, job.Request.Model, outcome.Units, outcome.UnitKind);
                metrics.RecordToolUnits(job.Request.Capability, job.Request.Model, outcome.Units, outcome.UnitKind);

                // Phase 57's second unit, on the same work rather than instead of it — phase 42's
                // audio precedent, where a transcription meters seconds and a synthesis characters.
                // The quota was spent above in megapixel-steps because that is what the card did;
                // this is the number a human asks for and it cannot be derived from the other one.
                if (outcome is { SecondaryUnits: > 0, SecondaryUnitKind: { } secondKind })
                {
                    usage.RecordUnits(
                        job.Client, job.Request.Capability, job.Request.Model, outcome.SecondaryUnits.Value, secondKind);
                    metrics.RecordToolUnits(
                        job.Request.Capability, job.Request.Model, outcome.SecondaryUnits.Value, secondKind);
                }
            }

            Forget(id);
        }
        catch (NodeDisconnectedException ex)
        {
            logger.LogWarning(
                "Image job {JobId} lost node {NodeId}: {Message}. It is NOT retried.",
                id,
                node.NodeId,
                ex.Message);

            Finish(id, ImageJobStates.Failed, ImageJobReasons.NodeLost, NodeLostMessage(node.NodeId));
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
            // Remove *this* job's claim on the node, never whatever claimed it since.
            busyNodes.TryRemove(new KeyValuePair<string, Guid>(node.NodeId, id));
            Pump();
        }
    }

    /// <summary>
    /// A worker that stopped because it was told to. Recognised from the <em>code</em> the node put
    /// on the result, never from the message — phase-29 D6's refusal, for the fourth time.
    /// </summary>
    private static bool IsCancellation(ToolResult result) =>
        !result.Success && string.Equals(result.ErrorCode, ToolErrorCodes.Cancelled, StringComparison.Ordinal);

    private void Finish(Guid id, string state, string reason, string? error, ImageJobFailure? failure = null)
    {
        store.TryTransition(id, state, reason, error, failure: failure);
        Count(id, state);
        Forget(id);
    }

    /// <summary>
    /// Counts a job that has reached a terminal state (phase 51, D2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in the store</b>, and it is the phase-28 D2 line: the store is in
    /// <c>InferHub.Shared</c> and has no metrics, no logger and no clock beyond a
    /// <c>TimeProvider</c> — a solo node runs the same class and has no <c>/metrics</c> at all. So
    /// the hub counts what the hub dispatched, from the two places a job can end.
    /// </para>
    /// <para>
    /// The duration is measured from <b>submission</b>, not from dispatch, because the number an
    /// operator cares about is how long the caller waited — a job that spent four minutes queued
    /// behind somebody else's batch was four minutes slow, whatever the GPU was doing.
    /// </para>
    /// </remarks>
    private void Count(Guid id, string state)
    {
        if (store.Peek(id) is not { } record || !ImageJobStates.IsTerminal(state))
        {
            return;
        }

        metrics.RecordImageJob(
            record.Model,
            state,
            ((record.CompletedAt ?? DateTimeOffset.UtcNow) - record.CreatedAt).TotalSeconds,

            // The job's own capability, folded to the medium the label carries (59 D2). Read from
            // the record rather than from a recipe catalogue, because the hub has never had one
            // (46 D6) and this phase is not where it gets one.
            CapabilityKinds.IsVideo(record.Capability)
                ? ImageRecipeMedia.Video
                : ImageRecipeMedia.Image);
    }

    internal static ImageJobFailure Failure(ImageOutcome outcome) => new(
        outcome.Status,
        outcome.Error,
        outcome.ErrorType,
        outcome.ErrorCode,
        outcome.ErrorParam,
        outcome.RetryAfterSeconds);

    /// <summary>
    /// Drops the hub-side context — and with it the client's admission lease — once a job can no
    /// longer move. The <em>record</em> stays in the store until retention lapses; this is only the
    /// part that holds a slot.
    /// </summary>
    private void Forget(Guid id)
    {
        if (store.Peek(id) is { IsTerminal: true } && pending.TryRemove(id, out var job))
        {
            job.Dispose();
        }
    }

    /// <summary>
    /// The per-job state the <em>hub</em> holds and the store deliberately does not: who the caller
    /// is, what they asked for, and the admission lease their job is occupying.
    /// </summary>
    /// <remarks>
    /// The lease is held for the life of the job rather than the life of the HTTP request, which is
    /// the honest reading of phase-25's concurrency limit: a client with a limit of two who submits
    /// five two-minute jobs is using two GPUs, not five, whatever their connection is doing.
    /// </remarks>
    private sealed record PendingImageJob(
        ResolvedClient Client,
        IImageRequest Request,
        IDisposable? AdmissionLease,
        CancellationTokenSource Cancellation) : IDisposable
    {
        public void Dispose()
        {
            AdmissionLease?.Dispose();
            Cancellation.Dispose();
        }
    }
}

/// <summary>
/// Retention and the LRU ceiling, on the <c>NodeReaper</c> shape (phase 47, D6).
/// </summary>
/// <remarks>
/// <b>The byte budget is not enforced here.</b> It is enforced on insert, inside the store: a timer
/// means the ceiling is a suggestion for one sweep interval, and one sweep interval of 4096² batches
/// is how a hub gets OOM-killed. What this does is the <em>time</em> half, which genuinely has
/// nowhere else to happen.
/// </remarks>
public sealed class ImageJobSweeper(ImageJobRegistry jobs, ILogger<ImageJobSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                jobs.Sweep();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The image job sweep failed");
            }
        }
    }
}
