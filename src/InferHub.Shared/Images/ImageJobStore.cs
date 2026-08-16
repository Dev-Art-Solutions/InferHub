using System.Text.Json;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;

namespace InferHub.Shared.Images;

/// <summary>
/// <c>Images:Jobs:*</c> — how long a finished job's bytes live, and how many of them there may be
/// (phase 47, D6).
/// </summary>
/// <remarks>
/// <b>Phase 56 made this rule 4's fourth recorded exception, and it is argued in the rule.</b> Under
/// the default — <see cref="Persistence"/> <c>none</c> — nothing here survives a restart and nothing
/// touches disk, which is v3.23 exactly. Under <c>file</c> a job's record and its bytes survive, for
/// <see cref="RetentionSeconds"/> and not one second longer: durability is survival of the same
/// window, never a longer one.
/// </remarks>
public sealed class ImageJobOptions
{
    public const string PersistenceNone = "none";

    public const string PersistenceFile = "file";

    public const string DefaultDataDirectory = "./data/images";

    /// <summary>
    /// <c>none</c> (default, byte-identical to v3.23) or <c>file</c> (phase 56). An unknown value
    /// fails startup naming the key rather than falling back silently.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <c>postgres</c>, unlike the other two persistence keys in this
    /// project: half a gigabyte of PNGs in a <c>bytea</c> column is WAL amplification per render and
    /// a <c>pg_dump</c> of the usage ledger's database that now contains pictures (D5). Symmetry
    /// with 43 D3 and 25 D2 is not a reason to put the wrong thing in a database.
    /// </remarks>
    public string Persistence { get; set; } = PersistenceNone;

    /// <summary>
    /// Where <c>file</c> writes. Relative by default so bare metal and Windows work; both images set
    /// the absolute path under their <c>chown app:app /data</c> — the container permissions trap for
    /// the seventh time (21 D7, 30 D3, 38 D4, 41 D5, 43 D3).
    /// </summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory;

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

    /// <summary><see cref="Persistence"/>, trimmed and lowercased; blank reads as <c>none</c>.</summary>
    public string NormalizedPersistence()
    {
        var value = (Persistence ?? string.Empty).Trim();

        return value.Length == 0 ? PersistenceNone : value.ToLowerInvariant();
    }

    /// <summary>Whether anything is written to disk at all.</summary>
    public bool Persists() =>
        string.Equals(NormalizedPersistence(), PersistenceFile, StringComparison.Ordinal);

    /// <summary>
    /// The <em>pure</em> half of validating this section, so the coordinator's validator and the
    /// node's cannot drift on what a legal value is (38 D3's line — the check is shared, the
    /// <c>IValidateOptions&lt;T&gt;</c> plumbing stays per host).
    /// </summary>
    /// <remarks>
    /// An unrecognised value is a startup failure rather than a fall back to <c>none</c>, for
    /// `Fleet:Profiles:Persistence`'s reason: falling back on a typo silently drops every job on the
    /// next restart, which is the failure the key exists to prevent.
    /// </remarks>
    public bool TryValidate(out string error)
    {
        var value = NormalizedPersistence();

        if (!string.Equals(value, PersistenceNone, StringComparison.Ordinal)
            && !string.Equals(value, PersistenceFile, StringComparison.Ordinal))
        {
            error = $"Images:Jobs:Persistence '{Persistence}' is not recognised; use 'none' or 'file'. "
                    + "There is deliberately no 'postgres': image bytes are not row data.";

            return false;
        }

        if (Persists() && string.IsNullOrWhiteSpace(DataDirectory))
        {
            error = "Images:Jobs:DataDirectory must be set when Images:Jobs:Persistence=file.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>One produced image, held in memory and nowhere else.</summary>
/// <param name="Projection">
/// <c>flat</c> or <c>equirectangular</c> (phase 49, D4). It rides on the image rather than on the
/// job because the content route hands back <em>one</em> image and has nowhere else to say it.
/// </param>
/// <param name="SeamDelta">
/// For an equirectangular render, how far its left and right columns are apart, 0–1, <b>as the bytes
/// beside it stand</b>. Measured by the worker — never <em>computed</em> here, because nothing in
/// this codebase's C# decodes a pixel (phase-46 D6).
/// </param>
/// <param name="SeamDeltaBefore">
/// What that measurement said before a repair ran (phase 55, D4). Present only alongside
/// <paramref name="SeamRepair"/>, and equal to <paramref name="SeamDelta"/> when the repair was
/// discarded for not improving it.
/// </param>
/// <param name="SeamRepair">
/// The mechanism the caller asked for and the worker ran — <c>blend</c> or <c>diffuse</c>. Absent
/// when nobody asked, which is the default and is byte-for-byte v3.22.
/// </param>
/// <param name="Seconds">
/// How long the produced media runs, for a video (phase 57). Null for every image, because absence is
/// a fact (28 D5) and a zero would read as a still.
/// </param>
public sealed record ImageJobImage(
    byte[] Bytes,
    string MediaType,
    ImageSize? Size,
    long? Seed,
    string? Projection = null,
    double? SeamDelta = null,
    int? Steps = null,
    double? SeamDeltaBefore = null,
    string? SeamRepair = null,
    double? Seconds = null);

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
    internal ImageJobRecord(
        Guid id,
        string clientId,
        string model,
        int count,
        DateTimeOffset createdAt,
        string capability = Contracts.CapabilityKinds.Image,
        ImageSize? size = null,
        double? seconds = null)
    {
        Id = id;
        ClientId = clientId;
        Model = model;
        Count = count;
        CreatedAt = createdAt;
        Capability = capability;
        Size = size;
        Seconds = seconds;
        State = ImageJobStates.Queued;
    }

    public Guid Id { get; }

    /// <summary>
    /// Which surface this job belongs to — <c>image</c>, <c>image-edit</c> or <c>video</c>
    /// (phase 57, D10).
    /// </summary>
    /// <remarks>
    /// One store holds all three, and the routes are scoped by this as well as by
    /// <see cref="ClientId"/>: a video job id handed to <c>GET /api/images/jobs/{id}</c> is a
    /// <c>404</c> and vice versa. Without it the images console would list video jobs it cannot
    /// render, and <c>/v1/videos/{id}</c> would happily answer about a picture.
    /// </remarks>
    public string Capability { get; }

    /// <summary>
    /// The geometry, once it is known: what the caller asked for, replaced by what the worker
    /// actually produced when it answers. Null while nobody has said — the recipe's own default is
    /// the node's business (46 D6), and inventing one here would be the hub declaring a model's
    /// native resolution.
    /// </summary>
    public ImageSize? Size { get; internal set; }

    /// <summary>The clip's duration, for a video. Null for an image (phase 57).</summary>
    public double? Seconds { get; internal set; }

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
/// The job store: in memory, bounded, expiring, read-once — and, since phase 56, <b>optionally
/// durable</b> (phase 47, D6; phase 56, D1).
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
/// <para>
/// <b>The archive is written under this lock, on purpose.</b> Every reason a job's bytes stop
/// existing — delivery, eviction, the retention sweep, a failure that clears them — decides it in
/// here, and a write scheduled for after the lock could be overtaken by the next one and leave the
/// disk describing a state the store has already left. The cost is a file write inside a critical
/// section that is otherwise microseconds; the thing being protected took a card ninety seconds.
/// Under the default archive every one of those calls is an empty method on a sealed type.
/// </para>
/// </remarks>
public sealed class ImageJobStore
{
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<Guid, ImageJobRecord> jobs = new();
    private readonly IImageJobArchive archive;

    /// <summary>Insertion order, which is the queue order. FIFO, deliberately (D5).</summary>
    private readonly List<Guid> order = new();

    public ImageJobStore(ImageJobOptions options, TimeProvider? time = null, IImageJobArchive? archive = null)
    {
        Options = options;
        this.time = time ?? TimeProvider.System;
        this.archive = archive ?? NoImageJobArchive.Instance;

        Restore();
    }

    public ImageJobOptions Options { get; }

    /// <summary>Raised after any observable change, so an SSE writer can wake. Never on the lock.</summary>
    public event Action<ImageJobRecord>? Changed;

    /// <summary>
    /// Admits a job, or refuses because the queue is full. A full queue is a <c>503</c> +
    /// <c>Retry-After</c> — the same status and header as every other limit in this codebase, so a
    /// client's retry logic behaves identically no matter which one it hit.
    /// </summary>
    /// <remarks>
    /// It takes the <em>request</em> rather than the four fields it used to, because phase 57 needs
    /// three more of them (the capability, the geometry and the duration) and a fifth and sixth
    /// positional argument is how a call site silently swaps two.
    /// </remarks>
    public bool TryCreate(Guid id, string clientId, IImageRequest request, out ImageJobRecord record)
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

            record = new ImageJobRecord(
                id,
                clientId,
                request.Model,
                request.Count,
                now,
                request.Capability,
                request.Size,
                request.Seconds)
            {
                LastTouched = now
            };
            jobs[id] = record;
            order.Add(id);
            Persist(record);
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
    /// The record, if it exists, belongs to this client <em>and</em> was submitted on the surface
    /// asking (phase 57).
    /// </summary>
    /// <remarks>
    /// The mismatch is a plain <c>null</c> and therefore the same <c>404</c> a nonexistent id earns,
    /// for phase-25 D4's reason: "that id is real but it is a picture" tells a caller something about
    /// an id they were not meant to reason about at all.
    /// </remarks>
    public ImageJobRecord? Find(Guid id, string clientId, Func<string, bool> capability)
    {
        var record = Find(id, clientId);

        return record is not null && capability(record.Capability) ? record : null;
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

    /// <summary>
    /// Every job this client owns, oldest first — narrowed to one family of capabilities when the
    /// caller is a surface rather than a bookkeeper (phase 57).
    /// </summary>
    public IReadOnlyList<ImageJobRecord> ForClient(string clientId, Func<string, bool>? capability = null)
    {
        lock (gate)
        {
            return order
                .Select(id => jobs.TryGetValue(id, out var record) ? record : null)
                .Where(record => record is not null
                    && string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
                    && (capability is null || capability(record.Capability)))
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

            Persist(record);
            changed = record;
        }

        Raise(changed);
        return true;
    }

    /// <summary>
    /// Per-step progress. Monotonic by construction: a lower step than the last is dropped.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not archived</b> (phase 56): a running job cannot be resumed anyway (D3), so
    /// persisting its step would be one file write per diffusion step to record a number nothing
    /// will ever read back.
    /// </remarks>
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
                Persist(candidate);
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

            // What was asked for is replaced by what was produced, because a recipe may clamp either
            // and the document has to describe the bytes the caller is about to fetch (46's
            // "meter what the worker produced", said about a field instead of a number).
            if (images.Count > 0)
            {
                record.Size = images[0].Size ?? record.Size;
                record.Seconds = images[0].Seconds ?? record.Seconds;
            }

            Persist(record);
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

                // Read once means read once *from the disk too* (phase 56, D4). The archive unlinks
                // the surplus files in this same call, because a delivered picture that survives as
                // a file is the rule quietly switched off.
                Persist(record);
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

                // Read once means read once *from the disk too* (phase 56, D4). The archive unlinks
                // the surplus files in this same call, because a delivered picture that survives as
                // a file is the rule quietly switched off.
                Persist(record);
                changed = record;
            }
        }

        Raise(changed);
        return image;
    }

    /// <summary>
    /// Forgets a job at its owner's request, bytes and archive file included (phase 57).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because OpenAI's Videos API has a <c>DELETE</c> and the images API has no such verb
    /// — phase 47's only way to be rid of a result is to fetch it or to wait. <b>The record is
    /// removed rather than expired</b>, because the dialect's <c>DELETE</c> means gone: a subsequent
    /// <c>GET</c> is a <c>404</c>, and leaving an <c>expired</c> tombstone behind would answer
    /// <c>410</c> with a sentence about retention that had nothing to do with what happened.
    /// </para>
    /// <para>
    /// Cancelling a job that is still running is the <em>caller's</em> separate step and is not done
    /// here: this class has never known how to reach a node.
    /// </para>
    /// </remarks>
    public bool Drop(Guid id, string clientId)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var record)
                || !string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
            {
                return false;
            }

            record.Images.Clear();
            jobs.Remove(id);
            order.Remove(id);
            Forget(id);
            return true;
        }
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
                    Persist(record);
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
                Forget(id);
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

    // ---- the archive (phase 56) ------------------------------------------------------------------

    /// <summary>
    /// Reads the archive back, <b>applying the retention window before a single byte is read</b>
    /// (D2), and resolving anything that was in flight to <c>failed</c> / <c>hub_restarted</c> (D3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Restarting a hub must never be a way to keep a picture longer than the window allows.</b>
    /// A job past its retention is deleted here rather than left for the five-second sweeper, because
    /// a window in which a week-old image is fetchable is a retention policy that is wrong for five
    /// seconds — and it would put the only enforcement of this phase's central promise on a timer
    /// that starts after the endpoints do. Resurrection is also the bug nobody would find: it happens
    /// in the crash-recovery path, on a box nobody is watching, and it looks like the feature working.
    /// </para>
    /// <para>
    /// A job that was <c>queued</c>, <c>running</c> or <c>cancelling</c> comes back <b>failed</b>,
    /// not re-dispatched: nothing durable holds the request (see <see cref="ArchivedImageJob"/>),
    /// because a prompt is content. That is 47 D7's sentence one level up — a silent re-dispatch
    /// would spend the GPU minutes and the ledger units twice for one request — and here it would
    /// additionally require writing down the thing rule 7 forbids.
    /// </para>
    /// </remarks>
    private void Restore()
    {
        if (!archive.Enabled)
        {
            return;
        }

        var now = time.GetUtcNow();
        var retention = TimeSpan.FromSeconds(Math.Max(1, Options.RetentionSeconds));
        var restored = new List<ImageJobRecord>();

        foreach (var archived in archive.Load().OrderBy(job => job.CreatedAt))
        {
            if (archived.CompletedAt is { } completedAt && now - completedAt >= retention)
            {
                archive.Delete(archived.Id);
                continue;
            }

            var record = new ImageJobRecord(
                archived.Id,
                archived.ClientId ?? string.Empty,
                archived.Model ?? string.Empty,
                archived.Count,
                archived.CreatedAt)
            {
                StartedAt = archived.StartedAt,
                CompletedAt = archived.CompletedAt,
                State = archived.State,
                Reason = archived.Reason,
                Error = archived.Error,
                Failure = archived.Failure,
                NodeId = archived.NodeId,
                Units = archived.Units,
                Summary = new ImageJobSummary(
                    archived.PromptAugmented,
                    archived.Trigger,
                    archived.Warnings ?? []),
                LastTouched = now
            };

            if (!ImageJobStates.IsTerminal(record.State))
            {
                record.State = ImageJobStates.Failed;
                record.Reason = ImageJobReasons.HubRestarted;
                record.Error = HubRestartedMessage;
                record.CompletedAt = now;
                record.Failure = null;
            }
            else if (record.State == ImageJobStates.Succeeded && archived.Images.Count > 0)
            {
                var bytes = archive.ReadImages(record.Id, archived.Images.Count);

                for (var index = 0; index < bytes.Count; index++)
                {
                    var image = archived.Images[index];

                    record.Images.Add(new ImageJobImage(
                        bytes[index],
                        image.MediaType ?? "image/png",
                        ImageSize.TryParse(image.Size, out var size, out _) ? size : (ImageSize?)null,
                        image.Seed,
                        image.Projection,
                        image.SeamDelta,
                        image.Steps,
                        image.SeamDeltaBefore,
                        image.SeamRepair));
                }

                // Bytes we could not read back are bytes that did not survive. Saying so is what
                // keeps a 410 meaning "you were too late" rather than handing somebody a truncated
                // PNG that fails in a viewer three steps later.
                if (record.Images.Count < archived.Images.Count)
                {
                    record.Images.Clear();
                    record.State = ImageJobStates.Expired;
                    record.Reason = ImageJobReasons.RetentionLapsed;
                }
            }

            jobs[record.Id] = record;
            order.Add(record.Id);
            restored.Add(record);
        }

        // The ceiling is a property of *now*, not of the run that wrote these — an operator who
        // lowered it while the hub was down gets the lower one honoured on the first boot, not on
        // the first render.
        var ceiling = Math.Max(1, Options.MaxRetainedBytes);

        foreach (var candidate in CompletedOldestFirst().ToArray())
        {
            if (RetainedLocked() <= ceiling)
            {
                break;
            }

            candidate.Images.Clear();
            candidate.State = ImageJobStates.Expired;
            candidate.Reason = ImageJobReasons.Evicted;
            candidate.Revision++;
        }

        foreach (var record in restored)
        {
            Persist(record);
        }
    }

    /// <summary>
    /// The sentence a client gets instead of a <c>404</c> that reads like a bug. It names the cause
    /// and the fact that nothing was retried, because those are the two things a caller has to know
    /// before deciding whether to submit it again.
    /// </summary>
    internal const string HubRestartedMessage =
        "the hub restarted while this job was in flight; it was not resumed, because nothing durable " +
        "holds a prompt (a prompt is content). Submit it again.";

    /// <summary>
    /// Writes a record through to the archive. <b>Caller holds the lock</b>, and the guard is on
    /// <see cref="IImageJobArchive.Enabled"/> rather than inside the archive so that the default
    /// path does not even build the document.
    /// </summary>
    private void Persist(ImageJobRecord record)
    {
        if (!archive.Enabled)
        {
            return;
        }

        archive.Save(
            new ArchivedImageJob(
                record.Id,
                record.ClientId,
                record.Model,
                record.Count,
                record.CreatedAt,
                record.StartedAt,
                record.CompletedAt,
                record.State,
                record.Reason,
                record.Error,
                record.NodeId,
                record.Units,
                record.Failure,
                record.Summary.PromptAugmented,
                record.Summary.Trigger,
                record.Summary.Warnings,
                record.Images
                    .Select(image => new ArchivedImageJobImage(
                        image.MediaType,
                        image.Size?.ToString(),
                        image.Seed,
                        image.Projection,
                        image.SeamDelta,
                        image.Steps,
                        image.SeamDeltaBefore,
                        image.SeamRepair))
                    .ToArray()),
            record.Images.Select(image => image.Bytes).ToArray());
    }

    /// <summary>Caller holds the lock.</summary>
    private void Forget(Guid id)
    {
        if (archive.Enabled)
        {
            archive.Delete(id);
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

    /// <summary>
    /// The same options the single-job document is written with, so a listing and a fetch of one of
    /// its rows cannot disagree about which keys are present (phase 51).
    /// </summary>
    public static JsonSerializerOptions JsonOptions => Json;

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
                    seamDelta = record.Images[index].SeamDelta,

                    // Phase 55, and absent unless a repair was asked for — the job document is the
                    // one place a client watching an async render can see that the number it is
                    // reading is a repaired one.
                    seamDeltaBefore = record.Images[index].SeamDeltaBefore,
                    seamRepair = record.Images[index].SeamRepair
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
