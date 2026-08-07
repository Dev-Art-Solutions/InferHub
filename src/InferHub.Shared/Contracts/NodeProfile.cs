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
    [property: JsonPropertyName("maxConcurrency")] int? MaxConcurrency = null,
    /// <summary>
    /// Phase 44. The corpus this node should be running, if any — the one field in a profile that
    /// <em>assigns</em> rather than narrows, and it is only sound because the hub records who owns
    /// the collection and excludes it from replication (phase-44 D1). Null leaves retrieval exactly
    /// as the box has it.
    /// </summary>
    [property: JsonPropertyName("retrieval")] RetrievalProfile? Retrieval = null,
    /// <summary>
    /// Phase 48. Image recipe id → whether this node should offer it. <c>false</c> narrows and
    /// always works; <c>true</c> is honoured only for a recipe the box already has, has accepted the
    /// licence of, and has the VRAM for.
    /// </summary>
    /// <remarks>
    /// This is the <b>third</b> thing a hub can narrow and the first one where the ceiling is
    /// arithmetic rather than a list: a profile that names a recipe needing 19 GB on a box that
    /// budgets 12 is refused with the numbers in the message, exactly as one naming a tool outside
    /// <c>Tools:Allowed</c> is refused with the list in it. The hub still narrows and never widens
    /// (phase-43 D1) — it cannot make a node accept a licence, find weights or grow a card.
    /// </remarks>
    [property: JsonPropertyName("imageRecipes")] IReadOnlyDictionary<string, bool>? ImageRecipes = null);

/// <summary>
/// A corpus the coordinator wants a node to host (phase 44): which engine, where it is, which
/// collections the node owns, and what to embed with.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no secret in here, and there will not be one</b> (D4). <see cref="CredentialRef"/> is
/// a <em>name</em>, resolved on the node against <c>LocalApi:Retrieval:Credentials:{ref}</c>. A hub
/// that carried the API key would be a secret distributor: the key would land in profile persistence
/// and in an admin API response, and every node the selector matched would be handed a credential it
/// may not need. A ref this node cannot resolve is a refusal naming the key — never a quiet fall back
/// to an unauthenticated connection.
/// </para>
/// <para>
/// <b>There is no field for a data directory either.</b> Where bytes land on a box is the operator's,
/// same as <c>Tools:Allowed</c> is in phase 41.
/// </para>
/// </remarks>
public sealed record RetrievalProfile(
    [property: JsonPropertyName("enabled")] bool Enabled,
    /// <summary><c>local</c> or <c>qdrant</c>. <c>postgres</c> is refused by name on a node (D2).</summary>
    [property: JsonPropertyName("provider")] string? Provider = null,
    /// <summary>Where the engine is, for an external provider. Ignored by <c>local</c>.</summary>
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("credentialRef")] string? CredentialRef = null,
    /// <summary>
    /// Collections this node owns. The hub has recorded itself out of them: it will not create them
    /// centrally, replicate to them or heal them (D1).
    /// </summary>
    [property: JsonPropertyName("collections")] IReadOnlyList<string>? Collections = null,
    [property: JsonPropertyName("embeddingModel")] string? EmbeddingModel = null);

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
