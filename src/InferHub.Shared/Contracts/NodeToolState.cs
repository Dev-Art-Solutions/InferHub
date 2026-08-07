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
    [property: JsonPropertyName("vram")] NodeVramState? Vram = null)
{
    public static NodeToolState Off(string nodeId) =>
        new(nodeId, Enabled: false, Array.Empty<NodeToolInfo>(), DateTimeOffset.UtcNow);
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
