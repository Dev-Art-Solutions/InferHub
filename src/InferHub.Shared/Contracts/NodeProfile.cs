using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What the coordinator says a node should be doing (phase 43): capabilities on or off, tools on or
/// off, models pulled or removed, concurrency set. It travels down the outbound SignalR connection
/// the node opened (phase-26 D1) — the hub still never dials a node.
/// </summary>
/// <remarks>
/// <para>
/// <b>A profile may narrow what a node does. It may never widen it.</b> Every field here is a
/// preference over the node's own configuration, and the node's configuration is the ceiling — see
/// <c>NodeProfileClamp</c>, which runs <em>on the node</em>. A hub can switch a tool off; it cannot
/// introduce one, name a command, an interpreter or a path, or raise a concurrency cap that is a
/// statement about hardware the operator owns.
/// </para>
/// <para>
/// It is <b>desired state, not a command</b> (D2). A node that reboots comes back in whatever state
/// it booted in, and asks for its profile again at registration; the hub answers with the same
/// revision and the node converges without an operator noticing anything happened.
/// </para>
/// </remarks>
public sealed record NodeProfile(
    [property: JsonPropertyName("name")] string Name,
    /// <summary>
    /// Monotonic per profile, bumped on every write. It is what makes convergence idempotent and
    /// reportable: a node that has already applied this number applies it again to no effect and
    /// says so, which is what makes the reconnect path safe to run unconditionally.
    /// </summary>
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("selector")] NodeProfileSelector Selector,
    /// <summary>
    /// Capability kind → whether this node should serve it. <c>false</c> narrows. <c>true</c> is a
    /// statement that the hub does not want it narrowed — it cannot re-enable a kind the node's own
    /// <c>Node:Capabilities:Disabled</c> switched off, and asking is a refusal with a reason.
    /// </summary>
    [property: JsonPropertyName("capabilities")] IReadOnlyDictionary<string, bool>? Capabilities = null,
    /// <summary>
    /// Tool id → whether this node should run it. <c>false</c> stops the pool. <c>true</c> is only
    /// honoured for an id already in <c>Tools:Allowed</c>, which is the grant phase-41 D2 made a list
    /// precisely so that this phase could not raise it.
    /// </summary>
    [property: JsonPropertyName("tools")] IReadOnlyDictionary<string, bool>? Tools = null,
    [property: JsonPropertyName("models")] NodeProfileModels? Models = null,
    /// <summary>Lowered, never raised. Null leaves the node's own cap alone.</summary>
    [property: JsonPropertyName("maxConcurrency")] int? MaxConcurrency = null);

/// <summary>
/// Which nodes a profile applies to: an exact node id, or an exact match on <b>every</b> label pair
/// given.
/// </summary>
/// <remarks>
/// There is deliberately no expression language, no glob and no regex. This selector decides what a
/// node is allowed to be asked to do, and a pattern dialect aimed at a security-relevant boundary is
/// phase-31 D1's footgun — the one where an operator writes something that reads correct and matches
/// one box more than they meant.
/// </remarks>
public sealed record NodeProfileSelector(
    [property: JsonPropertyName("nodeId")] string? NodeId = null,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>A selector that names nothing matches nothing — never everything.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(NodeId) && (Labels is null || Labels.Count == 0);

    public bool Matches(string nodeId, IReadOnlyDictionary<string, string>? labels)
    {
        if (IsEmpty)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(NodeId)
            && !string.Equals(NodeId.Trim(), nodeId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Labels is null || Labels.Count == 0)
        {
            return true;
        }

        foreach (var pair in Labels)
        {
            if (labels is null
                || !labels.TryGetValue(pair.Key, out var value)
                || !string.Equals(value?.Trim(), pair.Value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Models the node should hold, and models it should not. Both run through the phase-26
/// <c>ModelCommandExecutor</c> — there is no second pull path — so a backend that cannot manage
/// models refuses both with the reason it already gives.
/// </summary>
public sealed record NodeProfileModels(
    [property: JsonPropertyName("ensure")] IReadOnlyList<string>? Ensure = null,
    [property: JsonPropertyName("remove")] IReadOnlyList<string>? Remove = null);

/// <summary>
/// What the hub answers when a node asks for its profile at registration, and what it pushes when an
/// operator writes one.
/// </summary>
/// <remarks>
/// The three cases are distinct on purpose, because two of them look like "no profile" and mean
/// opposite things:
/// <list type="bullet">
/// <item><see cref="Profile"/> set — apply it.</item>
/// <item>Both null — no profile matches this node; revert to the box's own configuration.</item>
/// <item><see cref="Conflicts"/> set — two or more profiles match. The node keeps whatever it last
/// applied and changes nothing, and the hub reports the conflict. Merging them silently is how
/// somebody's node ends up in a state no single document explains (D4).</item>
/// </list>
/// </remarks>
public sealed record NodeProfileAssignment(
    [property: JsonPropertyName("profile")] NodeProfile? Profile = null,
    [property: JsonPropertyName("conflicts")] IReadOnlyList<string>? Conflicts = null)
{
    public static readonly NodeProfileAssignment None = new();

    public static NodeProfileAssignment Conflicted(IReadOnlyList<string> names) => new(null, names);

    public bool IsConflict => Conflicts is { Count: > 1 };
}
