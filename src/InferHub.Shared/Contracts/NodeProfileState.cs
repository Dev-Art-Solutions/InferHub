using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node did with the profile it was given (phase 43). Reported back up the same connection,
/// per item, never all-or-nothing: a profile that asks for one impossible thing and four possible
/// ones applies the four and reports the one (D6).
/// </summary>
public sealed record NodeProfileState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    /// <summary>Null when no profile applies — the node is running its own configuration.</summary>
    [property: JsonPropertyName("profileName")] string? ProfileName,
    [property: JsonPropertyName("revision")] long Revision,
    /// <summary>Human-readable, one line per item that took effect.</summary>
    [property: JsonPropertyName("applied")] IReadOnlyList<string> Applied,
    /// <summary>What the node would not do, and why. This is the load-bearing half.</summary>
    [property: JsonPropertyName("refusals")] IReadOnlyList<NodeProfileRefusal> Refusals,
    /// <summary>
    /// Started and still running — a model pull is minutes. Progress rides the phase-26
    /// <c>ModelCommandProgress</c> stream rather than being re-invented here.
    /// </summary>
    [property: JsonPropertyName("pending")] IReadOnlyList<string> Pending,
    /// <summary>
    /// The concurrency cap the node is actually registered at after clamping. The hub applies it to
    /// its registry entry, which is why lowering a cap does not need a re-registration.
    /// </summary>
    [property: JsonPropertyName("maxConcurrency")] int? MaxConcurrency,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc)
{
    /// <summary>
    /// <c>applied</c> | <c>refused</c> | <c>pending</c> | <c>none</c>. Not a wire field — the hub
    /// derives it for <c>/api/status</c>, and <c>conflict</c> is the hub's own answer rather than
    /// anything a node can report about itself.
    /// </summary>
    public string Status() => (Refusals.Count, Pending.Count, ProfileName) switch
    {
        (> 0, _, _) => "refused",
        (_, > 0, _) => "pending",
        (_, _, null) => "none",
        _ => "applied"
    };
}

/// <summary>One thing the node would not do, and the reason in the operator's words.</summary>
/// <remarks>
/// The reason names the configuration key that stopped it — <c>Tools:Allowed</c>,
/// <c>Node:Capabilities:Disabled</c>, <c>Node:MaxConcurrency</c> — because the operator's next
/// question is always "then what do I change, and where?".
/// </remarks>
public sealed record NodeProfileRefusal(
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("reason")] string Reason);
