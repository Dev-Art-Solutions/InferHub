using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.Images;

/// <summary>
/// <c>Images:Jobs:*</c> — how long a finished job's bytes live, and how many of them there may be
/// (phase 47, D6).
/// </summary>
/// <remarks>
/// <b>Design rule 4 is intact and this is not a fourth exception to it.</b> Nothing here survives a
/// restart, nothing here touches disk, and the retention is bounded by a number an operator sets
/// which defaults to five minutes. A future phase that wants <em>durable</em> jobs is a fourth
/// recorded exception and has to be argued in the rule, not added here.
/// </remarks>
public sealed class ImageJobOptions
{
    /// <summary>
    /// How long a completed job's record and bytes survive, whether or not anybody read them.
    /// Default 300s.
    /// </summary>
    /// <remarks>
    /// Five minutes is long enough for a client that watched the SSE stream to come back for the
    /// bytes over a slow link, and short enough that "where are my pictures kept" has the answer
    /// "nowhere, for five minutes" rather than a data-retention conversation.
    /// </remarks>
    public int RetentionSeconds { get; set; } = 300;

    /// <summary>
    /// The global ceiling on retained result bytes, LRU-evicting completed results — <b>never
    /// in-flight ones</b>. Default 512 MB.
    /// </summary>
    public long MaxRetainedBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Whether a delivered image is kept until it expires. Default <c>false</c>: read once, dropped
    /// on delivery.
    /// </summary>
    /// <remarks>
    /// It exists for the console's benefit and is documented as <em>the setting that makes the hub
    /// briefly an image cache</em>, in those words, because that is what it is.
    /// </remarks>
    public bool KeepAfterRead { get; set; }

    /// <summary>How many jobs may wait at once. Past it, a <c>503</c> + <c>Retry-After</c>.</summary>
    public int MaxQueueDepth { get; set; } = 32;
}

/// <summary>One produced image, held in memory and nowhere else.</summary>
/// <param name="Projection">
/// <c>flat</c> or <c>equirectangular</c> (phase 49, D4). It rides on the image rather than on the
/// job because the content route hands back <em>one</em> image and has nowhere else to say it.
/// </param>
/// <param name="SeamDelta">
/// For an equirectangular render, how far its left and right columns are apart, 0–1. Measured by the
/// worker and never repaired (D5) — and never <em>computed</em> here, because nothing in this
/// codebase's C# decodes a pixel (phase-46 D6).
/// </param>
public sealed record ImageJobImage(
    byte[] Bytes,
    string MediaType,
    ImageSize? Size,
    long? Seed,
    string? Projection = null,
    double? SeamDelta = null,
    int? Steps = null);

/// <summary>
/// What is true of a finished request as a whole, as opposed to of one of its images.
/// </summary>
/// <remarks>
/// <b>The trigger is a recipe constant and therefore not content.</b> That is what makes it safe to
/// put on a job document, in a response and in a log line — "why does this not look like a panorama"
/// is almost always "the trigger did not apply", and a diagnosis nobody can see is not one. The
/// prompt it was appended to is still never recorded anywhere (rule 7).
/// </remarks>
public sealed record ImageJobSummary(bool PromptAugmented, string? Trigger, IReadOnlyList<string> Warnings)
{
    public static ImageJobSummary None { get; } = new(false, null, []);
}

/// <summary>
/// How a failed job renders, carried on the record rather than re-derived per surface.
/// </summary>
/// <remarks>
/// <b>This is what keeps the synchronous route byte-identical to 3.14.</b> A worker that refuses a
/// size answers <c>invalid_request</c> and phase-46's <see cref="ImageRenderer"/> turns that into a
/// <c>400</c>; a busy tool answers with a <c>Retry-After</c> and it becomes a <c>503</c>. If a job
/// kept only "it failed, here is the sentence", every one of those would flatten to a <c>502</c> —
/// which is phase-29 D6's inference by the back door, arrived at by losing information rather than
/// by guessing. The node states the kind, the renderer decides the status, and the job carries the
/// decision to whichever surface asks.
/// </remarks>
public sealed record ImageJobFailure(
    int Status,
    string? Message,
    string ErrorType,
    string? ErrorCode,
    string? ErrorParam,
    int? RetryAfterSeconds);

/// <summary>
/// One job. Mutable, and every mutation goes through <see cref="ImageJobStore"/> under its lock —
/// a record that could be advanced from two places is a state machine with no owner.
/// </summary>
public sealed class ImageJobRecord
{
    internal ImageJobRecord(Guid id, string clientId, string model, int count, DateTimeOffset createdAt)
    {
        Id = id;
        ClientId = clientId;
        Model = model;
        Count = count;
        CreatedAt = createdAt;
        State = ImageJobStates.Queued;
    }

    public Guid Id { get; }

    /// <summary>
    /// Who may see it. Every route is scoped by this, and another client's id is a <c>404</c>
    /// byte-identical to one that does not exist (phase-25 D4).
    /// </summary>
    public string ClientId { get; }

    public string Model { get; }

    public int Count { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public string State { get; internal set; }

    public string? Reason { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>How this failure renders, when there is one. Null on every other state.</summary>
    public ImageJobFailure? Failure { get; internal set; }

    public string? NodeId { get; internal set; }

    public int? Step { get; internal set; }

    public int? TotalSteps { get; internal set; }

    public double Units { get; internal set; }

    /// <summary>Ticks upward on every observable change, so an SSE writer can skip a repeat.</summary>
    public long Revision { get; internal set; }

    /// <summary>The trigger and the warnings (phase 49). Empty until the job succeeds.</summary>
    public ImageJobSummary Summary { get; internal set; } = ImageJobSummary.None;

    internal List<ImageJobImage> Images { get; } = new();

    internal DateTimeOffset LastTouched { get; set; }

    public int ImageCount => Images.Count;

    public long RetainedBytes => Images.Sum(image => (long)image.Bytes.Length);

    public bool IsTerminal => ImageJobStates.IsTerminal(State);
}

/// <summary>
/// The job store: in memory, bounded, expiring, read-once, and <b>nothing touches disk, ever</b>
/// (phase 47, D6).
/// </summary>
/// <remarks>
/// <para>
/// It lives in <c>InferHub.Shared</c> rather than in the coordinator — a recorded deviation from the
/// phase brief's file path — for phase-38 D2's reason: solo mode serves the same five routes, and a
/// second implementation of "when do the bytes go away" is two answers to a question with one right
/// one. It is a plain class with no ASP.NET and no logging package in it, so design rule 2 holds and
/// <c>InferHub.Shared.csproj</c> is still empty.
/// </para>
/// <para>
/// <b>The byte budget is enforced on insert, not on a timer.</b> A timer means the ceiling is a
/// suggestion for one sweep interval, and one sweep interval of a 4096² batch is how a hub gets
/// OOM-killed.
/// </para>
/// </remarks>
public sealed class ImageJobStore(ImageJobOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<Guid, ImageJobRecord> jobs = new();

    /// <summary>Insertion order, which is the queue order. FIFO, deliberately (D5).</summary>
    private readonly List<Guid> order = new();

    public ImageJobOptions Options { get; } = options;

    /// <summary>Raised after any observable change, so an SSE writer can wake. Never on the lock.</summary>
    public event Action<ImageJobRecord>? Changed;

    /// <summary>
    /// Admits a job, or refuses because the queue is full. A full queue is a <c>503</c> +
    /// <c>Retry-After</c> — the same status and header as every other limit in this codebase, so a
    /// client's retry logic behaves identically no matter which one it hit.
    /// </summary>
    public bool TryCreate(Guid id, string clientId, string model, int count, out ImageJobRecord record)
    {
        var now = time.GetUtcNow();

        lock (gate)
        {
            var queued = 0;

            foreach (var jobId in order)
            {
                if (jobs.TryGetValue(jobId, out var existing) && existing.State == ImageJobStates.Queued)
                {
                    queued++;
                }
            }

            if (queued >= Math.Max(1, Options.MaxQueueDepth))
            {
                record = null!;
                return false;
            }

            record = new ImageJobRecord(id, clientId, model, count, now) { LastTouched = now };
            jobs[id] = record;
            order.Add(id);
        }

        Raise(record);
        return true;
    }

    /// <summary>
    /// The record, if it exists <em>and</em> belongs to this client. Nothing anywhere returns a
    /// record to somebody else; a caller that gets null renders the same 404 either way.
    /// </summary>
    public ImageJobRecord? Find(Guid id, string clientId)
    {
        lock (gate)
        {
            return jobs.TryGetValue(id, out var record)
                && string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
                    ? record
                    : null;
        }
    }

    /// <summary>
    /// The record regardless of who owns it. <b>For a host's own bookkeeping only</b> — no route
    /// may reach it, because the whole point of <see cref="Find(Guid, string)"/> is that a client
    /// cannot learn a job exists but is not theirs.
    /// </summary>
    public ImageJobRecord? Peek(Guid id)
    {
        lock (gate)
        {
            return jobs.GetValueOrDefault(id);
        }
    }

    /// <summary>Every job this client owns, oldest first.</summary>
    public IReadOnlyList<ImageJobRecord> ForClient(string clientId)
    {
        lock (gate)
        {
            return order
                .Select(id => jobs.TryGetValue(id, out var record) ? record : null)
                .Where(record => record is not null
                    && string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
                .Select(record => record!)
                .ToArray();
        }
    }

    /// <summary>The queued jobs, in line order. What the hub's pump reads.</summary>
    public IReadOnlyList<ImageJobRecord> Queued()
    {
        lock (gate)
        {
            return order
                .Select(id => jobs.TryGetValue(id, out var record) ? record : null)
                .Where(record => record is { State: ImageJobStates.Queued })
                .Select(record => record!)
                .ToArray();
        }
    }

    /// <summary>1-based place in line, or null once it is no longer queued.</summary>
    public int? QueuePosition(Guid id)
    {
        lock (gate)
        {
            var position = 0;

            foreach (var jobId in order)
            {
                if (!jobs.TryGetValue(jobId, out var record) || record.State != ImageJobStates.Queued)
                {
                    continue;
                }

                position++;

                if (record.Id == id)
                {
                    return position;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Advances a job, refusing a transition the table does not allow. False means the job already
    /// moved — a cancel that raced a result, most often, which is the normal case rather than an
    /// error.
    /// </summary>
    public bool TryTransition(
        Guid id,
        string to,
        string? reason = null,
        string? error = null,
        string? nodeId = null,
        ImageJobFailure? failure = null)
    {
        ImageJobRecord? changed = null;

        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var record) || !ImageJobStates.CanTransition(record.State, to))
            {
                return false;
            }

            record.State = to;
            record.Reason = reason ?? record.Reason;
            record.Error = error ?? record.Error;
            record.Failure = failure ?? record.Failure;
            record.NodeId = nodeId ?? record.NodeId;
            record.LastTouched = time.GetUtcNow();
            record.Revision++;

            if (to == ImageJobStates.Running)
            {
                record.StartedAt = record.LastTouched;
            }

            if (ImageJobStates.IsTerminal(to))
            {
                record.CompletedAt = record.LastTouched;
            }

            if (to is ImageJobStates.Failed or ImageJobStates.Cancelled or ImageJobStates.Expired)
            {
                // Nothing partial is kept. A cancelled run's latents are not an image, and a failed
                // one's are not either.
                record.Images.Clear();
            }

            changed = record;
        }

        Raise(changed);
        return true;
    }

    /// <summary>Per-step progress. Monotonic by construction: a lower step than the last is dropped.</summary>
    public bool ReportProgress(Guid id, int step, int? totalSteps)
    {
        ImageJobRecord? changed = null;

        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var record)
                || record.State is not (ImageJobStates.Running or ImageJobStates.Cancelling))
            {
                return false;
            }

            if (record.Step is { } last && step <= last)
            {
                return false;
            }

            record.Step = step;
            record.TotalSteps = totalSteps ?? record.TotalSteps;
            record.LastTouched = time.GetUtcNow();
            record.Revision++;
            changed = record;
        }

        Raise(changed);
        return true;
    }

    /// <summary>
    /// Lands a finished job's bytes, enforcing the ceiling <em>at this moment</em> by evicting the
    /// oldest completed results. In-flight jobs are never evicted — they hold no bytes yet, and a
    /// budget that could evict work in progress would be a budget that fails the request it was
    /// protecting.
    /// </summary>
    public bool TrySucceed(
        Guid id,
        IReadOnlyList<ImageJobImage> images,
        double units,
        ImageJobSummary? summary = null)
    {
        ImageJobRecord? changed = null;
        var evicted = new List<ImageJobRecord>();

        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var record)
                || !ImageJobStates.CanTransition(record.State, ImageJobStates.Succeeded))
            {
                return false;
            }

            var now = time.GetUtcNow();
            var incoming = images.Sum(image => (long)image.Bytes.Length);
            var ceiling = Math.Max(1, Options.MaxRetainedBytes);

            // Make room before admitting, oldest completed first. A result larger than the whole
            // ceiling still lands: refusing it would mean a size an operator configured for is a
            // size the hub cannot deliver, and the next insert evicts it anyway.
            foreach (var candidate in CompletedOldestFirst())
            {
                if (RetainedLocked() + incoming <= ceiling)
                {
                    break;
                }

                if (candidate.Id == id || candidate.Images.Count == 0)
                {
                    continue;
                }

                candidate.Images.Clear();
                candidate.State = ImageJobStates.Expired;
                candidate.Reason = ImageJobReasons.Evicted;
                candidate.CompletedAt ??= now;
                candidate.Revision++;
                evicted.Add(candidate);
            }

            record.State = ImageJobStates.Succeeded;
            record.CompletedAt = now;
            record.LastTouched = now;
            record.Units = units;
            record.Summary = summary ?? ImageJobSummary.None;
            record.Revision++;
            record.Images.Clear();
            record.Images.AddRange(images);
            changed = record;
        }

        foreach (var record in evicted)
        {
            Raise(record);
        }

        Raise(changed);
        return true;
    }

    /// <summary>
    /// Hands over one image and, unless <c>KeepAfterRead</c>, drops it. Null means the job is not
    /// this client's, is not finished, or its bytes are gone — the caller renders the difference.
    /// </summary>
    /// <summary>
    /// Every image at once, with the same read-once rule. This is what the synchronous
    /// <c>/v1/images/generations</c> uses, and routing it through the same store is what makes "the
    /// synchronous route quietly keeps a copy" a state this hub cannot be in.
    /// </summary>
    public IReadOnlyList<ImageJobImage> TryTakeAll(Guid id, string clientId)
    {
        ImageJobRecord? changed = null;
        ImageJobImage[] images;

        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var record)
                || !string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
                || record.State != ImageJobStates.Succeeded
                || record.Images.Count == 0)
            {
                return [];
            }

            images = record.Images.ToArray();
            record.LastTouched = time.GetUtcNow();

            if (!Options.KeepAfterRead)
            {
                record.Images.Clear();
                record.State = ImageJobStates.Expired;
                record.Reason = ImageJobReasons.Delivered;
                record.Revision++;
                changed = record;
            }
        }

        Raise(changed);
        return images;
    }

    public ImageJobImage? TryTakeContent(Guid id, string clientId, int index)
    {
        ImageJobRecord? changed = null;
        ImageJobImage? image;

        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var record)
                || !string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
                || record.State != ImageJobStates.Succeeded
                || index < 0
                || index >= record.Images.Count)
            {
                return null;
            }

            image = record.Images[index];
            record.LastTouched = time.GetUtcNow();

            if (!Options.KeepAfterRead)
            {
                record.Images.Clear();
                record.State = ImageJobStates.Expired;
                record.Reason = ImageJobReasons.Delivered;
                record.Revision++;
                changed = record;
            }
        }

        Raise(changed);
        return image;
    }

    /// <summary>
    /// Drops the bytes of everything past its retention window, and forgets a record entirely once
    /// it has been expired for a second window — otherwise a long-lived hub accumulates one small
    /// record per image it ever made, which is a leak with a slow fuse.
    /// </summary>
    public IReadOnlyList<ImageJobRecord> Sweep()
    {
        var now = time.GetUtcNow();
        var retention = TimeSpan.FromSeconds(Math.Max(1, Options.RetentionSeconds));
        var expired = new List<ImageJobRecord>();

        lock (gate)
        {
            var forget = new List<Guid>();

            foreach (var id in order)
            {
                if (!jobs.TryGetValue(id, out var record) || record.CompletedAt is not { } completedAt)
                {
                    continue;
                }

                if (now - completedAt < retention)
                {
                    continue;
                }

                if (record.State != ImageJobStates.Expired)
                {
                    record.Images.Clear();
                    record.State = ImageJobStates.Expired;
                    record.Reason ??= ImageJobReasons.RetentionLapsed;
                    record.Revision++;
                    expired.Add(record);
                    continue;
                }

                if (now - completedAt >= retention + retention)
                {
                    forget.Add(id);
                }
            }

            foreach (var id in forget)
            {
                jobs.Remove(id);
                order.Remove(id);
            }
        }

        foreach (var record in expired)
        {
            Raise(record);
        }

        return expired;
    }

    /// <summary>Total retained bytes. Reported on <c>/api/status</c> and scraped.</summary>
    public long RetainedBytes()
    {
        lock (gate)
        {
            return RetainedLocked();
        }
    }

    public int ActiveCount()
    {
        lock (gate)
        {
            return jobs.Values.Count(record => !record.IsTerminal);
        }
    }

    private long RetainedLocked() => jobs.Values.Sum(record => record.RetainedBytes);

    private IEnumerable<ImageJobRecord> CompletedOldestFirst() =>
        jobs.Values
            .Where(record => record.State == ImageJobStates.Succeeded && record.Images.Count > 0)
            .OrderBy(record => record.LastTouched);

    private void Raise(ImageJobRecord? record)
    {
        if (record is null)
        {
            return;
        }

        try
        {
            Changed?.Invoke(record);
        }
        catch (Exception)
        {
            // A subscriber that throws is an SSE writer whose client walked away. It must not take
            // the store's caller — which is a dispatch loop — with it.
        }
    }
}

/// <summary>
/// The wire shape of a job, decided once so a hub and a solo node cannot describe one differently
/// (phase-46 D6's <c>ImageRenderer</c> reasoning, applied to a second document).
/// </summary>
public static class ImageJobView
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public const string ContentType = "application/json";

    public static string Render(ImageJobRecord record, int? queuePosition) =>
        JsonSerializer.Serialize(Describe(record, queuePosition), Json);

    public static object Describe(ImageJobRecord record, int? queuePosition) => new
    {
        id = record.Id.ToString(),
        state = record.State,
        model = record.Model,
        n = record.Count,
        createdAt = record.CreatedAt,
        startedAt = record.StartedAt,
        completedAt = record.CompletedAt,
        queuePosition,
        node = record.NodeId,
        step = record.Step,
        totalSteps = record.TotalSteps,

        // Present only once there is something to fetch, so "is it ready" is answerable from the
        // shape rather than from the state name plus a rule.
        images = record.State == ImageJobStates.Succeeded && record.ImageCount > 0
            ? Enumerable.Range(0, record.ImageCount)
                .Select(index => new
                {
                    index,
                    url = $"/api/images/jobs/{record.Id}/content/{index}",
                    size = record.Images[index].Size?.ToString(),
                    seed = record.Images[index].Seed,
                    bytes = record.Images[index].Bytes.Length,

                    // Phase 49. A viewer picks a renderer from this rather than from the aspect
                    // ratio, which is what everyone does today and is wrong for every 2:1 photo.
                    projection = record.Images[index].Projection,
                    seamDelta = record.Images[index].SeamDelta
                })
                .ToArray()
            : null,
        megapixelSteps = record.Units > 0 ? record.Units : (double?)null,

        // Present whenever the recipe HAS a trigger, false included: a client that had to infer
        // "nothing was appended" from an absent key is a client guessing about its own prompt (D2).
        // A recipe with no trigger reports nothing rather than a permanent false.
        promptAugmented = record.Summary.Trigger is null ? (bool?)null : record.Summary.PromptAugmented,
        trigger = record.Summary.Trigger,
        warnings = record.Summary.Warnings.Count > 0 ? record.Summary.Warnings : null,
        reason = record.Reason,
        error = record.Error,

        // The worker's own code, so a job-watching client can act on the *kind* of failure without
        // reading the sentence — the same reason the synchronous route can still render a 400.
        errorCode = record.Failure?.ErrorCode
    };
}
