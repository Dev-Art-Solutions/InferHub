using System.Collections.Concurrent;
using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Cluster;

/// <summary>A model one member's nodes can serve, and the organisation of the node that holds it (130).</summary>
public sealed record PeerModel(ModelInfo Model, string? Organisation);

/// <summary>Another member of an active-active cluster as its last heartbeat described it.</summary>
public sealed record ClusterPeer(string Instance, Uri Url, TimeSpan SeenAgo, IReadOnlyList<PeerModel> Models);

/// <summary>One row of the members table: who, where, how long ago, and what its nodes serve (JSON of <see cref="PeerModel"/>).</summary>
public sealed record ClusterMemberRow(string Instance, string? Url, TimeSpan SeenAgo, string? Models);

/// <summary>
/// Phase 132. The other members of an active-active cluster that are alive now, and what their nodes can serve — refreshed
/// on every heartbeat tick from the members table, so it is never older than one renew interval. Empty in standby mode and
/// on a lone coordinator, which makes every caller a no-op there.
/// </summary>
public sealed class ClusterPeers
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IReadOnlyList<ClusterPeer> live = [];
    private readonly ConcurrentDictionary<(string Peer, string Outcome), long> forwarded = new();

    public IReadOnlyList<ClusterPeer> Live => Volatile.Read(ref live);

    /// <summary>
    /// The first live member whose nodes serve <paramref name="model"/> to the caller's organisation, if one does. Read under
    /// the request's <see cref="OrganisationScope"/>, so a model only a node of another organisation holds is not a reason to
    /// pass a request on; the member it would go to applies the same rule again.
    /// </summary>
    public ClusterPeer? FindFor(string model)
    {
        var scope = OrganisationScope.Current;

        return Live.FirstOrDefault(peer => peer.Models.Any(held =>
            NodeRegistry.ModelNamesMatch(held.Model.Name, model)
            && (scope is null || scope.Allows(held.Organisation, held.Model.Name, null))));
    }

    /// <summary>The models the other members serve to the caller's organisation, one entry per name, for the listings.</summary>
    public IReadOnlyList<ModelInfo> ModelsForCaller()
    {
        var scope = OrganisationScope.Current;

        return Live
            .SelectMany(peer => peer.Models)
            .Where(held => scope is null || scope.Allows(held.Organisation, held.Model.Name, null))
            .GroupBy(held => held.Model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Model with { Slots = null })
            .ToArray();
    }

    /// <summary>Replaces the live set from the members table: alive within <paramref name="ttl"/>, not this instance.</summary>
    internal void Replace(IEnumerable<ClusterMemberRow> rows, string self, TimeSpan ttl)
    {
        var next = new List<ClusterPeer>();

        foreach (var row in rows)
        {
            if (string.Equals(row.Instance, self, StringComparison.Ordinal)
                || row.SeenAgo >= ttl
                || !Uri.TryCreate(row.Url?.TrimEnd('/') + "/", UriKind.Absolute, out var url))
            {
                continue;
            }

            next.Add(new ClusterPeer(row.Instance, url, row.SeenAgo, Parse(row.Models)));
        }

        Volatile.Write(ref live, next.OrderBy(peer => peer.Instance, StringComparer.Ordinal).ToArray());
    }

    internal void Clear() => Volatile.Write(ref live, []);

    internal static string Describe(IReadOnlyCollection<PeerModel> models) => JsonSerializer.Serialize(models, Json);

    private static IReadOnlyList<PeerModel> Parse(string? models)
    {
        if (string.IsNullOrWhiteSpace(models))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PeerModel>>(models, Json) ?? [];
        }
        catch (JsonException)
        {
            // A member on a newer release may describe its models differently; it is still a member, it just offers nothing here.
            return [];
        }
    }

    /// <summary>
    /// <paramref name="outcome"/>: <c>served</c> (the member answered), <c>unreachable</c> (it did not, and this member answered
    /// instead), <c>missing</c> (a job id it did not hold).
    /// </summary>
    internal void Count(string peer, string outcome) => forwarded.AddOrUpdate((peer, outcome), 1, (_, count) => count + 1);

    public IReadOnlyCollection<KeyValuePair<(string Peer, string Outcome), long>> Forwarded() => forwarded.ToArray();
}
