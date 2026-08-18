using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node's tool runtime is doing (phase 41, reported from phase 45): which manifests it
/// loaded, which of them <c>Tools:Allowed</c> lets it start, what state each pool is in, and the
/// last thing that went wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this fills was phase 41's, not the console's.</b> Until v3.13 the only thing the hub
/// learned about a node's tools was the capability declaration folded into its model report — so a
/// manifest sitting on a box unnamed in <c>Tools:Allowed</c>, a pool that had given up, and a pool a
/// profile had suspended were all the same thing at the hub: nothing. Every one of those is a
/// question an operator asks out loud ("I put the file there and nothing happened"), and phase-41 D2
/// answers it in a log line on a box the operator is not looking at.
/// </para>
/// <para>
/// It is the phase-44 D6 mailbox, verbatim: the node reports, the hub records, and <b>the hub never
/// asks</b> — a status page that dials the fleet cannot answer when the fleet is what is broken.
/// A stale block is the honest failure mode and <see cref="AtUtc"/> says so.
/// </para>
/// </remarks>
public sealed record NodeToolState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    /// <summary><c>Tools:Enabled</c>. False means no runtime was ever constructed on this box.</summary>
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("tools")] IReadOnlyList<NodeToolInfo> Tools,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    /// <summary>
    /// The VRAM arithmetic this box is running under (phase 48), or null when no budget is declared.
    /// </summary>
    /// <remarks>
    /// <b>Null rather than zeros</b> — phase-28 D5 for the fifth time. A node with no declared
    /// budget has not measured anything and has no gate; reporting <c>budgetMiB: 0</c> would put a
    /// number on a dashboard that reads as "this box has no VRAM" rather than "nobody said".
    /// </remarks>
    [property: JsonPropertyName("vram")] NodeVramState? Vram = null,

    /// <summary>
    /// Every image recipe on the box and <em>why</em> each one is or is not offered (phase 51).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the gap phase 48 left and phase 51 is where it shows.</b> A recipe whose licence
    /// nobody accepted, or one too big for the declared budget, is simply <em>not declared</em>
    /// (48 D2/D5) — which is the right routing behaviour and the worst possible diagnostic: at the
    /// hub it is indistinguishable from a recipe that does not exist, from one whose weights are
    /// still downloading, and from a typo. Each of those has a different fix and the operator has no
    /// way to tell which they have.
    /// </para>
    /// <para>
    /// So the node states it. Same mailbox as everything else here (phase-44 D6): the node reports
    /// on its own refresh loop and the hub never asks. Empty on a node with no image recipes, which
    /// is every node that is not running the diffusion tool.
    /// </para>
    /// </remarks>
    [property: JsonPropertyName("images")] IReadOnlyList<NodeImageRecipeState>? Images = null)
{
    public static NodeToolState Off(string nodeId) =>
        new(nodeId, Enabled: false, Array.Empty<NodeToolInfo>(), DateTimeOffset.UtcNow);
}

/// <summary>
/// One image recipe on a node, and the answer to "why can I not use it?" (phase 51, D1).
/// </summary>
/// <param name="Offered">
/// Whether the fleet can route at it right now. Everything below explains a <c>false</c>.
/// </param>
/// <param name="Reason">One of <see cref="ImageRecipeReasons"/>. <c>ok</c> when it is offered.</param>
public sealed record NodeImageRecipeState(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("offered")] bool Offered,
    [property: JsonPropertyName("reason")] string Reason,
    /// <summary>
    /// The capability kinds this recipe is <em>currently offered under</em> — <c>image</c>,
    /// <c>image-edit</c>, or both.
    /// </summary>
    /// <remarks>
    /// Read from what the pools declare rather than from the recipe's <c>operations</c> field, and
    /// deliberately so: phase-48's catalogue note says the node parses exactly three things out of a
    /// recipe file — id, licence and VRAM — and nothing here needs that to change. What the fleet
    /// can route at is a fact the node already has. Empty for a recipe that is not offered, which is
    /// the honest answer rather than "it could do these if only".
    /// </remarks>
    [property: JsonPropertyName("kinds")] IReadOnlyList<string> Kinds,
    [property: JsonPropertyName("vramMiB")] int VramMiB,
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("licenseUrl")] string? LicenseUrl = null,
    [property: JsonPropertyName("quantization")] string? Quantization = null,

    /// <summary>
    /// What this recipe produces — <c>image</c> or <c>video</c> (phase 59, D1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Video recipes ride in this list rather than in one of their own.</b> Phase 57 filtered them
    /// out of it and said so, because a video row in a panel that draws pictures is wrong — but that
    /// is a <em>rendering</em> problem and the console is where it is now solved. The four reasons in
    /// <see cref="ImageRecipeReasons"/> are already the right four for a clip: the licence, the
    /// budget, a profile, or weights that are not there yet, each with the fix it had for a picture.
    /// A second array would be a second mailbox to keep in step and a second copy of that list.
    /// </para>
    /// <para>
    /// <b>Absent means <c>image</c></b> (40 D1, fifth use), so a v3.26 node reporting into a v3.27
    /// hub says exactly what it used to say and is read exactly as it used to be read.
    /// </para>
    /// </remarks>
    [property: JsonPropertyName("media")] string? Media = null);

/// <summary>
/// What a recipe produces, as the hub spells it (phase 59, D1).
/// </summary>
/// <remarks>
/// The node has its own copy of these two words in <c>ImageRecipeInfo</c>, where they are read out
/// of a recipe file. This is not that: it is the vocabulary of the <em>report</em>, and it exists so
/// a console, a metric label and a test do not each write <c>"video"</c> as a literal.
/// <see cref="Normalize"/> is where "absent means image" lives — one place, so a payload from a
/// v3.26 node cannot be read as one thing by the strip and another by the panel.
/// </remarks>
public static class ImageRecipeMedia
{
    public const string Image = "image";

    public const string Video = "video";

    public static string Normalize(string? media) =>
        string.Equals(media?.Trim(), Video, StringComparison.OrdinalIgnoreCase) ? Video : Image;

    public static bool IsVideo(string? media) => Normalize(media) == Video;
}

/// <summary>
/// Why a recipe is not offered. A short list on purpose: each entry exists because the <b>fix is
/// different</b>, and a reason nobody can act on differently is a reason that should not be here.
/// </summary>
public static class ImageRecipeReasons
{
    /// <summary>Offered. The fleet can route at it.</summary>
    public const string Ok = "ok";

    /// <summary>
    /// Its licence is not permissive and is not in <c>Tools:Image:AcceptedLicenses</c> (48 D5).
    /// The fix is a human reading a licence, which is why the id and the URL travel with it.
    /// </summary>
    public const string Unlicensed = "unlicensed";

    /// <summary>
    /// It does not fit <c>Node:Vram:BudgetMiB</c> minus the reserve (48 D2). The fix is a bigger
    /// card, a smaller reserve, or a quantized recipe — and the numbers are here to choose between
    /// them.
    /// </summary>
    public const string OverBudget = "over-budget";

    /// <summary>
    /// A coordinator profile switched it off (43 D6). The fix is on the hub, not on the box, and
    /// that distinction is the whole reason this is not merged into <see cref="NotReady"/>.
    /// </summary>
    public const string Narrowed = "narrowed";

    /// <summary>
    /// The node allows it and no worker offers it: weights still fetching, a fetch that failed, a
    /// recipe not marked <c>cpuViable</c> on a CPU-only box, or a pool that is not running. The
    /// fix is in the node's log, and this reason is what tells an operator to go and read it.
    /// </summary>
    public const string NotReady = "not-ready";
}

/// <summary>
/// What a node says about its card (phase 48, D1/D2): what the operator declared, what is held back,
/// and what is on it right now.
/// </summary>
/// <remarks>
/// <see cref="MeasuredMiB"/> is the worker's own <c>torch.cuda.mem_get_info()</c> reading and is
/// reported <em>beside</em> the declared figure rather than instead of it, precisely so a
/// disagreement is visible to whoever can fix it. Nothing routes, budgets or admits on it.
/// </remarks>
public sealed record NodeVramState(
    [property: JsonPropertyName("budgetMiB")] int BudgetMiB,
    [property: JsonPropertyName("reserveMiB")] int ReserveMiB,
    [property: JsonPropertyName("measuredMiB")] int? MeasuredMiB,
    /// <summary>Models believed to be on the card, and how much each is budgeted at.</summary>
    [property: JsonPropertyName("resident")] IReadOnlyList<NodeResidentModel> Resident);

public sealed record NodeResidentModel(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("vramMiB")] int VramMiB,
    [property: JsonPropertyName("inUse")] bool InUse);

/// <summary>One manifest on the box, and what became of it.</summary>
public sealed record NodeToolInfo(
    [property: JsonPropertyName("id")] string Id,
    /// <summary>
    /// Whether <c>Tools:Allowed</c> names it. <b>This is the ceiling a coordinator can never raise</b>
    /// (phase-41 D2), which is exactly why it is worth showing beside the state: a tool that is not
    /// allowed is not broken, and the fix is on the node rather than in a profile.
    /// </summary>
    [property: JsonPropertyName("allowed")] bool Allowed,
    /// <summary>
    /// <see cref="Running"/> | <see cref="Suspended"/> | <see cref="Stopped"/> | <see cref="NotAllowed"/>.
    /// The four are deliberately distinct: three of them mean "this node will not do that work" and
    /// each has a different fix.
    /// </summary>
    [property: JsonPropertyName("state")] string State,
    /// <summary>What this pool currently offers — live, so a pool that gave up offers nothing.</summary>
    [property: JsonPropertyName("capabilities")] IReadOnlyList<NodeCapability> Capabilities,
    [property: JsonPropertyName("maxWorkers")] int MaxWorkers,
    /// <summary>Warm workers this pool is holding, idle plus leased.</summary>
    [property: JsonPropertyName("workers")] int Workers,
    /// <summary>Workers currently serving a request.</summary>
    [property: JsonPropertyName("busy")] int Busy,
    [property: JsonPropertyName("requests")] long Requests,
    [property: JsonPropertyName("failures")] long Failures,
    /// <summary>
    /// The last thing that went wrong, in the worker's own words. A traceback's first line is the
    /// single most useful thing a tool author sees (phase-41 D5), and it is on a box nobody is
    /// tailing — so it travels.
    /// </summary>
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("lastErrorAtUtc")] DateTimeOffset? LastErrorAtUtc)
{
    public const string Running = "running";

    /// <summary>Switched off by a coordinator profile (phase-43 D6). Resumable in place.</summary>
    public const string Suspended = "suspended";

    /// <summary>Gave up after its restart budget (phase-41 D6). Still probing.</summary>
    public const string Stopped = "stopped";

    /// <summary>Loaded from the manifest directory, not named in <c>Tools:Allowed</c>, never started.</summary>
    public const string NotAllowed = "not-allowed";
}
