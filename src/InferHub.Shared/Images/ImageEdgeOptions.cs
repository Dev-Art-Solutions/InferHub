using InferHub.Shared.Contracts;

namespace InferHub.Shared.Images;

/// <summary>
/// <c>Images:*</c> — what an image edge will accept before a single diffusion step runs (phase 46).
/// </summary>
/// <remarks>
/// <para>
/// <b>One class, one section name, bound by both hosts.</b> The coordinator and the solo node each
/// bind <c>Images</c> into this type, so a request refused on a hub is refused identically on a solo
/// node — the difference a parity suite is there to catch, prevented by construction instead. It is
/// a plain options class with no ASP.NET in it, so design rule 2 is intact; that is also why it can
/// live here at all.
/// </para>
/// <para>
/// The pattern deviates from <c>ToolEdgeOptions</c>/<c>ToolOptions</c>, which are two classes with
/// one section name, and deliberately: those two genuinely differ (a node's tool options carry a
/// restart budget the hub has no use for), while every key here means exactly the same thing on both
/// sides. Two copies of a class with identical fields is two places to forget a default.
/// </para>
/// </remarks>
public sealed class ImageEdgeOptions
{
    public const string SectionName = "Images";

    /// <summary>The absolute ceiling on <c>n</c>, whatever the size. Default 4.</summary>
    /// <remarks>
    /// A batch runs on <b>one</b> node, deliberately. Fanning it across nodes would mean four
    /// different seeds' worth of scheduling for a request a client thinks is atomic, and a partial
    /// failure would have no honest status to return.
    /// </remarks>
    public int MaxBatch { get; set; } = ImageLimits.DefaultMaxBatch;

    /// <summary>
    /// The upper-bound size of everything one request may send back. Default 25 MB, matching
    /// <c>Tools:MaxAttachmentBytes</c>.
    /// </summary>
    public long MaxResponseBytes { get; set; } = ToolAttachmentLimits.DefaultMaxBytes;

    /// <summary>
    /// The upper bound on what one <em>edit</em> may send in — the picture and the mask together
    /// (phase 50, D4). Default 25 MB, matching <c>Tools:MaxAttachmentBytes</c>.
    /// </summary>
    /// <remarks>
    /// It is a separate key from <see cref="MaxResponseBytes"/> because the two directions are
    /// separate risks with separate arithmetic: outbound is <c>n</c> renders of a declared size, and
    /// inbound is one upload somebody chose the size of. Each individual part is <em>also</em>
    /// capped by <c>Tools:MaxAttachmentBytes</c>, because that is what the node enforces — and a
    /// request that passed the edge and failed at the node is a worse error than one refused here.
    /// </remarks>
    public long MaxRequestBytes { get; set; } = ToolAttachmentLimits.DefaultMaxBytes;

    /// <summary>
    /// How long the synchronous <c>/v1/images/generations</c> waits for its own job before giving up
    /// (phase 47). Past it: a <c>503</c> naming the async route. Default 120s.
    /// </summary>
    /// <remarks>
    /// The route is <b>unchanged</b> for anyone who never reads the phase-47 notes — internally it
    /// became "submit a job and wait for it", which is a refactor with no observable difference and
    /// a test (<c>ImageSyncCompatTests</c>) that says so. What this bound adds is an honest ceiling:
    /// before it, a request against a 50-step SDXL job simply held a connection open until the
    /// node-facing dispatch timeout, and a proxy in the path usually cut it first with a status
    /// nobody could act on.
    /// </remarks>
    public int SyncMaxWaitSeconds { get; set; } = 120;

    /// <summary>The async job surface's own knobs (phase 47, D5/D6).</summary>
    public ImageJobOptions Jobs { get; set; } = new();

    /// <summary>
    /// The effective limits, clamped by the attachment cap.
    /// </summary>
    /// <remarks>
    /// <b>The clamp is applied at use rather than rejected at validation, and that is the point.</b>
    /// <c>Tools:MaxAttachmentBytes</c> is what sizes the SignalR receive limit
    /// (<c>NodeHubLimits.ReceiveSizeFor</c>), and exceeding a SignalR message cap does not fail the
    /// message — it <em>tears the connection down</em>, which is how v3.10.0 shipped dead on arrival
    /// over a 300 KB WAV. So a response budget larger than the attachment cap cannot be expressed:
    /// an operator who raises one without the other gets no change rather than a fleet that drops
    /// nodes under load.
    /// </remarks>
    public ImageLimits Resolve(long maxAttachmentBytes) => new(
        Math.Max(1, MaxBatch),
        Math.Min(
            MaxResponseBytes > 0 ? MaxResponseBytes : ToolAttachmentLimits.DefaultMaxBytes,
            maxAttachmentBytes > 0 ? maxAttachmentBytes : ToolAttachmentLimits.DefaultMaxBytes),
        MaxRequestBytes > 0 ? MaxRequestBytes : ToolAttachmentLimits.DefaultMaxBytes);
}
