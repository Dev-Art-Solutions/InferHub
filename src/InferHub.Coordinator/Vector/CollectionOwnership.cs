using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Who is the authority for a collection name (phase 44, D1). <b>One place</b>, the way
/// <c>CollectionAccessPolicy</c> and <c>FleetSaturation</c> are one place.
/// </summary>
/// <remarks>
/// <para>
/// The invariant this exists to keep, and the sentence to check any future change against:
/// <b>one authority per collection name, and the hub knows who it is.</b>
/// </para>
/// <para>
/// Phase-38 D1 refused to boot a node that was both meshed and holding its own corpus, because such a
/// node would hold hub-derived replicas <em>and</em> an authority under the same names, and
/// <c>ReplicationCoordinator</c> would eventually overwrite a collection its operator believed they
/// owned. That reasoning is <b>not</b> reversed. What changed is that the hub can now be the one who
/// assigns the corpus, and can therefore be the one who <em>knows</em>: a name recorded here as
/// node-owned is refused a hub-side create, never replicated to and never healed. Disjointness is
/// structural rather than a convention somebody has to remember.
/// </para>
/// <para>
/// It is in memory, like every other registry on the coordinator (rule 4). Losing it costs the same
/// as losing a profile does (phase-43 D3): the profiles are re-read at startup and every node re-asks
/// for its own at registration, so ownership is re-derived from the documents that produced it rather
/// than from a second stored copy that could disagree with them.
/// </para>
/// </remarks>
public sealed class CollectionOwnership
{
    /// <summary>The owner of every name nobody has claimed.</summary>
    public const string Hub = "hub";

    private readonly ConcurrentDictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the collections a profile assigns to a node, replacing whatever that node owned
    /// before. A node that was assigned <c>a, b</c> and is then assigned <c>a</c> stops owning
    /// <c>b</c> — which is what makes a profile desired state here too (phase-43 D2).
    /// </summary>
    public void Assign(string nodeId, IReadOnlyList<string>? collections)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

        var owner = OwnerOf(nodeId);
        var wanted = new HashSet<string>(
            (collections ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in owners.ToArray())
        {
            if (string.Equals(pair.Value, owner, StringComparison.OrdinalIgnoreCase)
                && !wanted.Contains(pair.Key))
            {
                owners.TryRemove(pair.Key, out _);
            }
        }

        foreach (var name in wanted)
        {
            owners[name] = owner;
        }
    }

    /// <summary>Everything this node owned stops being node-owned. Used when retrieval is switched off.</summary>
    public void Release(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return;
        }

        var owner = OwnerOf(nodeId);

        foreach (var pair in owners.ToArray())
        {
            if (string.Equals(pair.Value, owner, StringComparison.OrdinalIgnoreCase))
            {
                owners.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary><c>hub</c>, or <c>node:{id}</c>.</summary>
    public string OwnerOfCollection(string collection) =>
        string.IsNullOrWhiteSpace(collection)
            ? Hub
            : owners.GetValueOrDefault(collection.Trim(), Hub);

    /// <summary>The node id that owns this collection, or null when the hub does.</summary>
    public string? NodeOwning(string collection)
    {
        var owner = OwnerOfCollection(collection);

        return owner.StartsWith("node:", StringComparison.OrdinalIgnoreCase)
            ? owner["node:".Length..]
            : null;
    }

    /// <summary>
    /// Whether the hub is the authority. <b>Replication and healing bind to this</b>, because a
    /// node-owned collection has no hub-side records to derive replicas from and pushing an empty
    /// set at its owner would be the hub deleting somebody's corpus.
    /// </summary>
    public bool IsHubOwned(string collection) => NodeOwning(collection) is null;

    /// <summary>Every node-owned name, with its owner. For <c>/api/status</c> and the console.</summary>
    public IReadOnlyDictionary<string, string> NodeOwned() =>
        owners.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The message a hub-side create gets for a name a node owns. It names the owner, because "409"
    /// on its own sends somebody looking for a collection that does not exist here — it exists
    /// somewhere else, on purpose.
    /// </summary>
    public string RefusalFor(string collection) =>
        $"collection '{collection}' is owned by {OwnerOfCollection(collection)} and cannot be created on the coordinator. A node-owned collection is created by the node that hosts it, and this hub deliberately holds no copy of it.";

    private static string OwnerOf(string nodeId) => $"node:{nodeId.Trim()}";

    /// <summary>
    /// Re-derives ownership from the profile book. Called after any profile write or delete, so the
    /// record follows the documents rather than accumulating beside them.
    /// </summary>
    /// <remarks>
    /// A node matched by two profiles is a conflict (phase-43 D4) and gets <em>no</em> profile, so it
    /// owns nothing here either — the two states cannot disagree, because both read the same match.
    /// </remarks>
    public void Rebuild(IEnumerable<(string NodeId, NodeProfile? Profile)> assignments)
    {
        owners.Clear();

        foreach (var (nodeId, profile) in assignments)
        {
            if (profile?.Retrieval is { Enabled: true } retrieval)
            {
                Assign(nodeId, retrieval.Collections);
            }
        }
    }
}
