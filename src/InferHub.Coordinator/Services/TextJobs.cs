using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Observability;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>Phase 150: a chat or a generate that runs as a job — accepted at once, followed by id, read when it is done.</summary>
public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Off answers every <c>/api/jobs</c> route with a 404 naming this key.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Jobs the hub holds at once, finished ones included. At the cap the oldest finished job goes; with none finished a submit is a 503.</summary>
    public int MaxJobs { get; set; } = 256;

    /// <summary>Unfinished jobs one client may have. Past it a submit is a 429.</summary>
    public int MaxActivePerClient { get; set; } = 16;

    /// <summary>How long a finished job and its answer stay in memory. Nothing is ever written to disk.</summary>
    public int RetentionSeconds { get; set; } = 600;

    /// <summary>How long a job may wait for a fleet that answers 503 (full, a group reassembling, nobody sharing now) before it fails with that 503.</summary>
    public int QueueSeconds { get; set; } = 600;

    /// <summary>The dispatch deadline of a job, in place of <c>Dispatcher:TimeoutSeconds</c> — a job is what a caller submits when the answer takes long.</summary>
    public int DeadlineSeconds { get; set; } = 3600;
}

public sealed class JobsOptionsValidator : IValidateOptions<JobsOptions>
{
    public ValidateOptionsResult Validate(string? name, JobsOptions options)
    {
        var failures = new List<string>();

        void AtLeast(int value, int minimum, string key)
        {
            if (value < minimum)
            {
                failures.Add($"{JobsOptions.SectionName}:{key} must be >= {minimum} (got {value}).");
            }
        }

        AtLeast(options.MaxJobs, 1, nameof(JobsOptions.MaxJobs));
        AtLeast(options.MaxActivePerClient, 1, nameof(JobsOptions.MaxActivePerClient));
        AtLeast(options.RetentionSeconds, 1, nameof(JobsOptions.RetentionSeconds));
        AtLeast(options.QueueSeconds, 0, nameof(JobsOptions.QueueSeconds));
        AtLeast(options.DeadlineSeconds, 1, nameof(JobsOptions.DeadlineSeconds));

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Phase 150 D2: what a followed job hands the dispatcher, riding an <see cref="AsyncLocal{T}"/> as the audit scope
/// does (125 D2) — the dispatcher is reached through <see cref="InferenceCore"/> and its failover, and neither
/// passes anything along.
/// </summary>
public sealed class JobWatch(Guid jobId, DispatchDeadline deadline, Action<RoutableNode> dispatched, Action<InferenceJobProgress> reported)
{
    private static readonly AsyncLocal<JobWatch?> current = new();

    public static JobWatch? Current => current.Value;

    /// <summary>The id the caller holds; a dispatch's own id changes when it fails over.</summary>
    public Guid JobId { get; } = jobId;

    public DispatchDeadline Deadline { get; } = deadline;

    public void Dispatched(RoutableNode node) => dispatched(node);

    public void NodeReported(InferenceJobProgress progress) => reported(progress);

    public static IDisposable Enter(JobWatch watch)
    {
        var previous = current.Value;
        current.Value = watch;
        return new Restore(previous);
    }

    private sealed class Restore(JobWatch? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}

public static class TextJobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status) => status is Succeeded or Failed or Cancelled;
}

public static class TextJobStage
{
    /// <summary>At the hub: admission, a place on a node, a group coming together.</summary>
    public const string Waiting = "waiting";

    /// <summary>Sent to a node that has not said it has it yet.</summary>
    public const string Sent = "sent";

    /// <summary>The node has it; its engine has produced nothing yet (loading the model, reading the prompt).</summary>
    public const string Preparing = "preparing";

    public const string Generating = "generating";

    public const string Done = "done";
}

/// <summary>One job. Everything a status shows is a count, a name or a time; the request and the answer are held apart and never logged (rule 7).</summary>
public sealed class TextJob
{
    private readonly object gate = new();
    private readonly StringBuilder text = new();
    private readonly StringBuilder thinking = new();
    private string? toolCallsJson;
    private string? terminalJson;
    private TaskCompletionSource<bool> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? firstPieceAt;

    internal TextJob(Guid id, string clientId, string kind, string model, DateTimeOffset now)
    {
        Id = id;
        ClientId = clientId;
        Kind = kind;
        Model = model;
        CreatedAt = UpdatedAt = now;
    }

    public Guid Id { get; }
    public string ClientId { get; }
    public string Kind { get; }
    public string Model { get; }
    public DateTimeOffset CreatedAt { get; }

    public string Status { get; private set; } = TextJobStatus.Queued;
    public string Stage { get; private set; } = TextJobStage.Waiting;
    public string? Detail { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? NodeId { get; private set; }
    public string? NodeName { get; private set; }
    public int Attempts { get; private set; }
    public int Pieces { get; private set; }
    public int? Tokens { get; private set; }
    public double? TokensPerSecond { get; private set; }
    public int? NodePieces { get; private set; }
    public DateTimeOffset? NodeReportedAt { get; private set; }
    public string? ServedBy { get; private set; }
    public int? ErrorStatus { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int? RetryAfterSeconds { get; private set; }
    public int Version { get; private set; }

    public bool IsTerminal => TextJobStatus.IsTerminal(Status);

    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Completes on the next change. Read it, then compare <see cref="Version"/>, then wait.</summary>
    public Task Changed
    {
        get
        {
            lock (gate)
            {
                return changed.Task;
            }
        }
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
        var previous = changed;
        changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult(true);
    }

    internal void Waiting(string? why, DateTimeOffset now)
    {
        lock (gate)
        {
            if (IsTerminal)
            {
                return;
            }

            Status = TextJobStatus.Queued;
            Stage = TextJobStage.Waiting;
            Detail = why;
            Touch(now);
        }
    }

    internal void Dispatched(RoutableNode node, DateTimeOffset now)
    {
        lock (gate)
        {
            if (IsTerminal)
            {
                return;
            }

            Status = TextJobStatus.Running;
            Stage = TextJobStage.Sent;
            Detail = null;
            NodeId = node.NodeId;
            NodeName = node.Name;
            NodePieces = null;
            NodeReportedAt = null;
            Attempts++;
            StartedAt ??= now;
            Touch(now);
        }
    }

    internal void ServedOutsideTheFleet(string servedBy, DateTimeOffset now)
    {
        lock (gate)
        {
            if (IsTerminal)
            {
                return;
            }

            ServedBy = servedBy;

            if (Status == TextJobStatus.Queued)
            {
                // A provider answered: no node, so no dispatch ever said "running".
                Status = TextJobStatus.Running;
                Stage = TextJobStage.Sent;
                Detail = null;
                Attempts++;
                StartedAt ??= now;
            }

            Touch(now);
        }
    }

    internal void NodeReported(InferenceJobProgress progress, DateTimeOffset now)
    {
        lock (gate)
        {
            if (IsTerminal || !string.Equals(progress.NodeId, NodeId, StringComparison.Ordinal))
            {
                return;
            }

            NodeReportedAt = now;
            NodePieces = progress.Pieces;

            if (Stage == TextJobStage.Sent)
            {
                Stage = TextJobStage.Preparing;
            }

            if (Stage == TextJobStage.Preparing)
            {
                Detail = progress.Detail;
            }

            Touch(now);
        }
    }

    /// <summary>Takes one piece of the answer. Returns the failure a terminal error piece carries, if it is one.</summary>
    internal (int Status, string Message)? Accept(InferenceChunk chunk, DateTimeOffset now)
    {
        string? piece = null;
        string? thought = null;
        string? tools = null;
        (int, string)? failure = null;
        int? evalCount = null;
        long? evalDuration = null;

        try
        {
            using var document = JsonDocument.Parse(chunk.ResponseJson);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : (int?)null;
                    failure = (InferenceResult.HttpStatusOf(status),
                        InferenceCore.ReadableNodeError(error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText()));
                }
                else if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    piece = StringOf(message, "content");
                    thought = StringOf(message, "thinking");

                    if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
                    {
                        tools = calls.GetRawText();
                    }
                }
                else
                {
                    piece = StringOf(root, "response");
                    thought = StringOf(root, "thinking");
                }

                if (root.TryGetProperty("eval_count", out var count) && count.TryGetInt32(out var n))
                {
                    evalCount = n;
                }

                if (root.TryGetProperty("eval_duration", out var duration) && duration.TryGetInt64(out var d))
                {
                    evalDuration = d;
                }
            }
        }
        catch (JsonException)
        {
        }

        lock (gate)
        {
            if (IsTerminal || failure is not null)
            {
                return failure;
            }

            Stage = TextJobStage.Generating;
            Detail = null;

            if (!string.IsNullOrEmpty(piece) || !string.IsNullOrEmpty(thought))
            {
                firstPieceAt ??= now;
                Pieces++;
                text.Append(piece);
                thinking.Append(thought);

                var seconds = (now - firstPieceAt.Value).TotalSeconds;
                TokensPerSecond = seconds >= 1 ? Math.Round(Pieces / seconds, 2) : TokensPerSecond;
            }

            toolCallsJson = tools ?? toolCallsJson;

            if (chunk.Done)
            {
                terminalJson = chunk.ResponseJson;
                Tokens = evalCount;

                if (evalCount is > 0 && evalDuration is > 0)
                {
                    TokensPerSecond = Math.Round(evalCount.Value / (evalDuration.Value / 1e9), 2);
                }
            }

            Touch(now);
            return null;
        }
    }

    internal bool Finish(string status, DateTimeOffset now, int? errorStatus = null, string? code = null, string? message = null, int? retryAfter = null)
    {
        lock (gate)
        {
            if (IsTerminal)
            {
                return false;
            }

            Status = status;
            Stage = TextJobStage.Done;
            Detail = null;
            CompletedAt = now;
            ErrorStatus = errorStatus;
            ErrorCode = code;
            ErrorMessage = message;
            RetryAfterSeconds = retryAfter;

            if (status != TextJobStatus.Succeeded)
            {
                // A failed or cancelled job keeps no part of an answer.
                text.Clear();
                thinking.Clear();
                toolCallsJson = terminalJson = null;
            }

            Touch(now);
            return true;
        }
    }

    /// <summary>
    /// The answer as the blocking route would have given it — the terminal piece with the whole text in it — or,
    /// while the job runs, what has arrived so far with <c>done: false</c>.
    /// </summary>
    public string ResultJson()
    {
        lock (gate)
        {
            JsonObject root;

            try
            {
                root = terminalJson is null ? [] : JsonNode.Parse(terminalJson) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                root = [];
            }

            root["model"] ??= Model;

            if (Kind == "chat")
            {
                var message = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() };

                if (thinking.Length > 0)
                {
                    message["thinking"] = thinking.ToString();
                }

                if (toolCallsJson is not null)
                {
                    message["tool_calls"] = JsonNode.Parse(toolCallsJson);
                }

                root["message"] = message;
            }
            else
            {
                root["response"] = text.ToString();

                if (thinking.Length > 0)
                {
                    root["thinking"] = thinking.ToString();
                }
            }

            root["done"] = Status == TextJobStatus.Succeeded;
            return root.ToJsonString();
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Why a submit was not accepted: the status and the sentence.</summary>
public sealed record TextJobRefusal(int Status, string Message, int? RetryAfterSeconds = null);

/// <summary>
/// Phase 150: the hub's text jobs. A job is the streamed request <see cref="InferenceCore"/> already knows how to
/// admit, route, queue, fail over and meter — run by the hub instead of by a held connection, with its status kept
/// where a caller, the node and the hub can all update and read it. In memory only (rule 4): a hub restart loses
/// every job, and a finished one goes after <see cref="JobsOptions.RetentionSeconds"/>.
/// </summary>
public sealed class TextJobRegistry(
    IOptions<JobsOptions> options,
    IRouter router,
    INodeRegistry nodes,
    IDispatcher dispatcher,
    IProviderDispatcher providers,
    Metrics metrics,
    ILogger<TextJobRegistry> logger,
    ShardPeerPublisher? groups = null,
    TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<Guid, TextJob> jobs = new();
    private readonly object admit = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public JobsOptions Options => options.Value;

    public bool Enabled => options.Value.Enabled;

    internal (TextJob? Job, TextJobRefusal? Refusal) Submit(
        InferenceCore.ClientContext context,
        string kind,
        string rawJson,
        string model,
        string? conversationKey,
        ProviderSteer steer)
    {
        var now = clock.GetUtcNow();
        TextJob job;

        lock (admit)
        {
            var mine = jobs.Values.Count(j => !j.IsTerminal && j.ClientId == context.Client.Id);

            if (mine >= options.Value.MaxActivePerClient)
            {
                return (null, new TextJobRefusal(
                    StatusCodes.Status429TooManyRequests,
                    $"this client already has {mine} unfinished jobs ({JobsOptions.SectionName}:{nameof(JobsOptions.MaxActivePerClient)}); "
                    + "wait for one to finish or cancel one",
                    5));
            }

            if (jobs.Count >= options.Value.MaxJobs)
            {
                var oldest = jobs.Values.Where(j => j.IsTerminal).OrderBy(j => j.CompletedAt).FirstOrDefault();

                if (oldest is null)
                {
                    return (null, new TextJobRefusal(
                        StatusCodes.Status503ServiceUnavailable,
                        $"the hub holds {jobs.Count} unfinished jobs ({JobsOptions.SectionName}:{nameof(JobsOptions.MaxJobs)})",
                        15));
                }

                Remove(oldest);
            }

            job = new TextJob(Guid.NewGuid(), context.Client.Id, kind, model, now);
            jobs[job.Id] = job;
        }

        // The kind, the model and the client — never the request (rule 7).
        logger.LogInformation("Accepted {Kind} job {JobId} ({Model}) for client {ClientId}", kind, job.Id, model, context.Client.Id);

        var organisation = OrganisationScope.Current;

        // The job outlives the request that submitted it, so it takes nothing of that request's flow with it but
        // the organisation it is routed in (130), entered again below.
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => RunAsync(job, context, rawJson, conversationKey, steer, organisation));
        }

        return (job, null);
    }

    public TextJob? Find(Guid id, string clientId) =>
        jobs.TryGetValue(id, out var job) && job.ClientId == clientId ? job : null;

    public IReadOnlyList<TextJob> ForClient(string clientId) =>
        jobs.Values.Where(j => j.ClientId == clientId).OrderByDescending(j => j.CreatedAt).ToArray();

    public IReadOnlyList<TextJob> All() => jobs.Values.OrderByDescending(j => j.CreatedAt).ToArray();

    public (int Queued, int Running, int Finished) Counts()
    {
        int queued = 0, running = 0, finished = 0;

        foreach (var job in jobs.Values)
        {
            switch (job.Status)
            {
                case TextJobStatus.Queued: queued++; break;
                case TextJobStatus.Running: running++; break;
                default: finished++; break;
            }
        }

        return (queued, running, finished);
    }

    /// <summary>Asks a job to stop. The node is told through the dispatch's own token; the job is cancelled here at once.</summary>
    public bool Cancel(TextJob job)
    {
        if (!job.Finish(TextJobStatus.Cancelled, clock.GetUtcNow(), code: "cancelled", message: "the job was cancelled"))
        {
            return false;
        }

        try
        {
            job.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        return true;
    }

    public void Remove(TextJob job)
    {
        if (jobs.TryRemove(job.Id, out _) && !job.IsTerminal)
        {
            Cancel(job);
        }
    }

    public void Sweep()
    {
        var cutoff = clock.GetUtcNow().AddSeconds(-options.Value.RetentionSeconds);

        foreach (var job in jobs.Values)
        {
            if (job is { IsTerminal: true, CompletedAt: { } completed } && completed < cutoff)
            {
                jobs.TryRemove(job.Id, out _);
            }
        }
    }

    /// <summary>The group a job's model is, as the hub sees it now — which member holds which layers and how far its load is (142 D2).</summary>
    public object? GroupOf(TextJob job)
    {
        if (job.IsTerminal || groups is null)
        {
            return null;
        }

        ShardGroupStatus? status;

        try
        {
            status = groups.GroupStatus().FirstOrDefault(g => string.Equals(g.Name, job.Model, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the shard groups for job {JobId}", job.Id);
            return null;
        }

        if (status is null)
        {
            return null;
        }

        return new
        {
            name = status.Name,
            head = status.Head,
            ready = status.Ready,
            state = status.Reported?.State,
            why = status.Why ?? status.Reported?.Error,
            members = status.Reported?.Layers is { } layers
                ? layers.Select(l => (object)new { node = l.Name, layers = l.Count, expectMiB = l.ExpectMiB, sentMiB = l.SentMiB }).ToArray()
                : status.Members.Select(m => (object)new { node = m }).ToArray()
        };
    }

    private async Task RunAsync(
        TextJob job,
        InferenceCore.ClientContext context,
        string rawJson,
        string? conversationKey,
        ProviderSteer steer,
        OrganisationScope? organisation)
    {
        var settings = options.Value;
        var cancellation = job.Cancellation.Token;
        var queueUntil = job.CreatedAt.AddSeconds(settings.QueueSeconds);

        var watch = new JobWatch(
            job.Id,
            new DispatchDeadline(
                CapabilityKinds.ForJobKind(job.Kind),
                TimeSpan.FromSeconds(settings.DeadlineSeconds),
                $"{JobsOptions.SectionName}:{nameof(JobsOptions.DeadlineSeconds)}"),
            node => job.Dispatched(node, clock.GetUtcNow()),
            progress => job.NodeReported(progress, clock.GetUtcNow()));

        using var inOrganisation = OrganisationScope.Enter(organisation);
        using var watched = JobWatch.Enter(watch);

        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();

                var outcome = await InferenceCore.DispatchAsync(
                    job.Kind, rawJson, job.Model, stream: true, conversationKey, context,
                    router, nodes, dispatcher, providers, metrics, logger, cancellation, steer);

                if (outcome.IsError)
                {
                    // D4: a 503 is the fleet saying "not now" — full, a group coming back together, an owner not
                    // sharing. A held connection gets it as the answer; a job waits for it to pass.
                    if (outcome.ErrorStatus == StatusCodes.Status503ServiceUnavailable && clock.GetUtcNow() < queueUntil)
                    {
                        job.Waiting(outcome.ErrorMessage, clock.GetUtcNow());
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(outcome.RetryAfterSeconds ?? 5, 1, 10)), clock, cancellation);
                        continue;
                    }

                    End(job, TextJobStatus.Failed, outcome.ErrorStatus, "refused", outcome.ErrorMessage, outcome.RetryAfterSeconds);
                    return;
                }

                if (outcome.ServedBy != InferenceCore.ServedByNode)
                {
                    job.ServedOutsideTheFleet(outcome.ServedBy, clock.GetUtcNow());
                }

                if (outcome.Stream is not { } stream)
                {
                    job.Accept(new InferenceChunk(job.Id, outcome.ResponseJson ?? "{}", true), clock.GetUtcNow());
                    End(job, TextJobStatus.Succeeded);
                    return;
                }

                await foreach (var chunk in stream.ReadAllAsync(cancellation))
                {
                    if (job.Accept(chunk, clock.GetUtcNow()) is { } failure)
                    {
                        End(job, TextJobStatus.Failed, failure.Status, "node_error", failure.Message);
                        return;
                    }

                    if (chunk.Done)
                    {
                        End(job, TextJobStatus.Succeeded);
                        return;
                    }
                }

                End(job, TextJobStatus.Failed, StatusCodes.Status502BadGateway, "node_error", "the answer ended before it was finished");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            End(job, TextJobStatus.Cancelled, code: "cancelled", message: "the job was cancelled");
        }
        catch (NodeDisconnectedException)
        {
            // Not retried, as an image job is not (47 D7): part of the answer was already produced and counted.
            End(job, TextJobStatus.Failed, StatusCodes.Status502BadGateway, "node_lost",
                $"node '{job.NodeName ?? job.NodeId}' disappeared while this job was running; it was not retried");
        }
        catch (TimeoutException ex)
        {
            End(job, TextJobStatus.Failed, StatusCodes.Status504GatewayTimeout, "deadline", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Job {JobId} failed", job.Id);
            End(job, TextJobStatus.Failed, StatusCodes.Status500InternalServerError, "hub_error", ex.Message);
        }
        finally
        {
            job.Cancellation.Dispose();
        }
    }

    private void End(TextJob job, string status, int? errorStatus = null, string? code = null, string? message = null, int? retryAfter = null)
    {
        if (job.Finish(status, clock.GetUtcNow(), errorStatus, code, message, retryAfter))
        {
            logger.LogInformation(
                "{Kind} job {JobId} ({Model}) {Status} after {Seconds:F1} s on {Node}, {Pieces} piece(s)",
                job.Kind, job.Id, job.Model, job.Status, (job.CompletedAt!.Value - job.CreatedAt).TotalSeconds,
                job.NodeName ?? job.ServedBy ?? "(no node)", job.Pieces);
        }
    }
}

public sealed class TextJobSweeper(TextJobRegistry jobs, ILogger<TextJobSweeper> logger) : BackgroundService
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
                logger.LogWarning(ex, "The job sweep failed");
            }
        }
    }
}
