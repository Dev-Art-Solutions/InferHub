using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// How a node shares itself with its network (phase 113): its owner's mode, and whether it takes new network jobs
/// now. Reported by the node through the 44 D6 mailbox (<c>ReportSharingState</c>); a hub that holds it routes no new
/// job to a paused node, as it routes none to a cordoned one. The node is the authority — the hub never sets it.
/// </summary>
public sealed record NodeSharingState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    /// <summary><c>always</c>, <c>idle</c> or <c>paused</c>.</summary>
    [property: JsonPropertyName("mode")] string Mode,
    /// <summary>True: send this node no new jobs.</summary>
    [property: JsonPropertyName("paused")] bool Paused,
    /// <summary>Why, in a sentence: "paused by its owner", "its owner is using it (2 local jobs)".</summary>
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("sinceUtc")] DateTimeOffset SinceUtc,
    [property: JsonPropertyName("localJobs")] int LocalJobs,
    [property: JsonPropertyName("networkJobs")] int NetworkJobs,
    /// <summary>Phase 116: when a pause with an end — idle time, sharing hours, the day's allowance — is due to lift. Null: not known.</summary>
    [property: JsonPropertyName("resumesAtUtc")] DateTimeOffset? ResumesAtUtc = null);
