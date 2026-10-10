using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node says about its own version and the newest release it could move to (phase 101).
/// </summary>
/// <remarks>
/// <para>
/// The phase-44 D6 mailbox once more: the node reports on its model loop and whenever its update state
/// changes, the hub records it, and <b>the hub never asks</b>. A node before v3.66 sends none, and the hub
/// shows the version from its registration exactly as it always did.
/// </para>
/// <para>
/// <b><see cref="CanApply"/> is about this box, <see cref="AllowFromHub"/> is about its operator.</b> A
/// Docker node can never apply (its updater is the image); an installed Windows service can, unless its
/// operator chose "never" in the setup. The console needs both to say why its button is grey.
/// </para>
/// </remarks>
public sealed record NodeUpdateState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    /// <summary>The running version, without build metadata (<c>3.66.0</c>).</summary>
    [property: JsonPropertyName("current")] string Current,
    /// <summary>The newest release above <see cref="Current"/> with a setup attached; null when none, or never checked.</summary>
    [property: JsonPropertyName("available")] string? Available,
    /// <summary>One of <see cref="NodeUpdatePhase"/>.</summary>
    [property: JsonPropertyName("state")] string State,
    /// <summary><c>Update:Check</c>: whether this node looks for releases at all.</summary>
    [property: JsonPropertyName("check")] bool Check,
    /// <summary><c>Update:Auto</c>: whether it applies one by itself.</summary>
    [property: JsonPropertyName("auto")] bool Auto,
    /// <summary><c>Update:AllowFromHub</c>: whether an admin may tell it to.</summary>
    [property: JsonPropertyName("allowFromHub")] bool AllowFromHub,
    /// <summary>Whether this box can apply an update at all (an installed Windows service run as an administrator).</summary>
    [property: JsonPropertyName("canApply")] bool CanApply,
    /// <summary>The sentence that goes with <c>canApply:false</c>.</summary>
    [property: JsonPropertyName("whyNot")] string? WhyNot,
    [property: JsonPropertyName("lastCheckedUtc")] DateTimeOffset? LastCheckedUtc,
    /// <summary>The last check's or apply's failure, as a sentence. Null after a clean one.</summary>
    [property: JsonPropertyName("lastError")] string? LastError,
    /// <summary>What the last applied update came to, e.g. <c>updated 3.65.0 → 3.66.0 at …</c>; survives the restart it caused.</summary>
    [property: JsonPropertyName("lastUpdate")] string? LastUpdate,
    /// <summary>The release page of <see cref="NodeUpdateState.Available"/>, for the console's link.</summary>
    [property: JsonPropertyName("releaseUrl")] string? ReleaseUrl,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

/// <summary>The values of <see cref="NodeUpdateState.State"/>.</summary>
public static class NodeUpdatePhase
{
    /// <summary>Never checked: <c>Update:Check</c> is off, or the first check has not run yet.</summary>
    public const string Unknown = "unknown";

    /// <summary>Asking the release feed now.</summary>
    public const string Checking = "checking";

    /// <summary>Checked; nothing newer with a setup attached.</summary>
    public const string UpToDate = "up-to-date";

    /// <summary>A newer release is there. With <c>auto</c>, it is waiting for this node to be idle.</summary>
    public const string Available = "available";

    /// <summary>Fetching and verifying the setup.</summary>
    public const string Downloading = "downloading";

    /// <summary>The setup has been started; the service is about to stop and come back on the new version.</summary>
    public const string Applying = "applying";

    /// <summary>The last check or apply failed — <see cref="NodeUpdateState.LastError"/> says how.</summary>
    public const string Failed = "failed";
}

/// <summary>What the hub asks a node to do about its version (phase 101).</summary>
public sealed record NodeUpdateCommand(
    /// <summary><see cref="KindCheck"/> or <see cref="KindApply"/>.</summary>
    [property: JsonPropertyName("kind")] string Kind,
    /// <summary>Who asked, for the node's log line.</summary>
    [property: JsonPropertyName("requestedBy")] string? RequestedBy = null)
{
    /// <summary>Look for a release now rather than at the next interval.</summary>
    public const string KindCheck = "check";

    /// <summary>Apply the newest release now. Refused by a node whose operator did not allow it from the hub.</summary>
    public const string KindApply = "apply";
}
