namespace InferHub.Shared.Images;

/// <summary>
/// Durability for image jobs (phase 56) — <b>rule 4's fourth recorded exception</b>, and the seam
/// that keeps it one. Two implementations, selected by <c>Images:Jobs:Persistence</c>, exactly as
/// <c>IProfileStore</c> (43 D3) and <c>IAffinityStore</c> (30 D2) are.
/// </summary>
/// <remarks>
/// <para>
/// Rule 4's own note on 47 D6 set the condition for becoming an exception: <em>the moment a result
/// survives a restart, "where are my pictures kept" stops having the answer "nowhere, for five
/// minutes" and becomes a data-retention question somebody has to own.</em> So the answer is written
/// down rather than avoided — with <c>file</c>, pictures are under <c>Images:Jobs:DataDirectory</c>
/// for <c>RetentionSeconds</c> and not one second longer (D2) — and <see cref="NoImageJobArchive"/>
/// is the default, under which nothing is created, opened or listed.
/// </para>
/// <para>
/// <b>Load is synchronous and happens once, at store construction</b>, which is <c>ProfileRegistry</c>'s
/// shape and for the same reason: a hub that answered <c>404</c> for a job it was about to load a
/// second later would be telling a client its picture never existed.
/// </para>
/// </remarks>
public interface IImageJobArchive
{
    /// <summary>
    /// Whether anything is written at all. Checked before a record is <em>built</em>, so the default
    /// path allocates nothing — a deployment that changes no config runs v3.23's code exactly.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Every archived job's <em>record</em>, in no particular order and <b>without its pixels</b>.
    /// The store applies the window to these before asking for a single byte (D2), so a hub that was
    /// down over a weekend never reads a gigabyte it is about to delete.
    /// </summary>
    IReadOnlyList<ArchivedImageJob> Load();

    /// <summary>
    /// The bytes of a job that survived the window. An image that cannot be read back is
    /// <b>absent</b>, not empty: the record then holds fewer images than it claimed, which the store
    /// renders as an expired job rather than as a zero-byte PNG somebody has to debug.
    /// </summary>
    IReadOnlyList<byte[]> ReadImages(Guid id, int count);

    /// <summary>
    /// Writes a job's record, and its images if it has any. An archived job with <b>fewer</b> images
    /// than last time — delivered, evicted, expired, or cleared by a failure — has the surplus files
    /// unlinked in the same call, which is what keeps read-once true of the disk as well as of the
    /// API (D4).
    /// </summary>
    void Save(ArchivedImageJob job, IReadOnlyList<byte[]> images);

    /// <summary>Forgets a job entirely: its record and every byte of it.</summary>
    void Delete(Guid id);
}

/// <summary>
/// The one place <c>Images:Jobs:Persistence</c> becomes an implementation, so a hub and a solo node
/// cannot answer "is this durable" differently (46 D6's parity-by-construction, fourth use).
/// </summary>
public static class ImageJobArchives
{
    public static IImageJobArchive Create(ImageJobOptions options, Action<string, Exception>? onError = null) =>
        options.Persists()
            ? new FileImageJobArchive(options.DataDirectory, onError)
            : NoImageJobArchive.Instance;
}

/// <summary>The default. An image job lives as long as the process does, like every other counter here.</summary>
public sealed class NoImageJobArchive : IImageJobArchive
{
    public static NoImageJobArchive Instance { get; } = new();

    public bool Enabled => false;

    public IReadOnlyList<ArchivedImageJob> Load() => [];

    public IReadOnlyList<byte[]> ReadImages(Guid id, int count) => [];

    public void Save(ArchivedImageJob job, IReadOnlyList<byte[]> images)
    {
    }

    public void Delete(Guid id)
    {
    }
}

/// <summary>
/// A job as it survives a restart. <b>There is no field here that could hold a prompt</b> — and that
/// is the decision rather than an omission (phase 56, D3).
/// </summary>
/// <remarks>
/// <para>
/// A prompt is content (rule 7, phase 46), and this is the first phase in the project able to write
/// content to a disk. So the refusal is structural, exactly as <c>UsageRecord</c>'s is (25 D3):
/// there is no prompt, no negative prompt, no uploaded picture and no mask, and there is deliberately
/// no flag to add one, <em>because a field is an invitation</em>. The direct consequence is that an
/// interrupted job cannot be resumed — the hub would have to have written down what to render — and
/// it comes back <c>failed</c> with <see cref="ImageJobReasons.HubRestarted"/> instead.
/// </para>
/// <para>
/// <see cref="Error"/> is kept, and it is not a hole in that: it is the <em>worker's</em> sentence
/// about a size, a licence or a busy card, which <c>ImageRenderer</c> never lets echo the caller's
/// words back (<c>revised_prompt</c> is null by policy). Same line as 49 D2's trigger phrase — a
/// constant of the model may be recorded, the caller's own words may not.
/// </para>
/// </remarks>
public sealed record ArchivedImageJob(
    Guid Id,
    string ClientId,
    string Model,
    int Count,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string State,
    string? Reason,
    string? Error,
    string? NodeId,
    double Units,
    ImageJobFailure? Failure,
    bool PromptAugmented,
    string? Trigger,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ArchivedImageJobImage> Images);

/// <summary>
/// What is true of one archived image apart from its pixels. <b>The bytes are a file of their own</b>,
/// so this document cannot hold a picture and that file cannot hold a sentence.
/// </summary>
public sealed record ArchivedImageJobImage(
    string MediaType,
    string? Size,
    long? Seed,
    string? Projection,
    double? SeamDelta,
    int? Steps,
    double? SeamDeltaBefore,
    string? SeamRepair);
