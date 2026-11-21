using System.Globalization;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Fit;

/// <summary>
/// Phase 144 D2: one GGUF file in a node's group models directory (<c>Node:Shards:ModelsDir</c>), as its header says —
/// <see cref="Name"/> is what a group's <c>Model</c> names, <see cref="MiB"/> the whole file (every part of a split one).
/// </summary>
public sealed record ShardModelFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mib")] long MiB,
    [property: JsonPropertyName("layers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Layers = null,
    [property: JsonPropertyName("kvBytesPerToken"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? KvBytesPerToken = null);

/// <summary>
/// A node the hub could place in a group (phase 144): what it offers groups, the file it holds (if it does), whether its owner
/// lets it head, its RPC protocol, and the round trips it measured to its peers — only peers this hub listed for it.
/// </summary>
public sealed record PlacementCandidate(
    string NodeId,
    string Name,
    long OfferMiB,
    ShardModelFile? File,
    bool MayHead,
    string? Protocol,
    IReadOnlyList<ShardPeerState> Peers);

/// <summary>
/// The hub's choice for a group (phase 144): a head and its members in order, or <see cref="Why"/> not. <see cref="Notes"/>
/// says how it was chosen — the head, each member's round trip from the head, what was left out and why.
/// </summary>
public sealed record ShardPlacementResult(
    string? HeadId,
    IReadOnlyList<string> MemberIds,
    string? Why,
    IReadOnlyList<string> Notes,
    double? RoundTripMs = null,
    long NeedMiB = 0)
{
    public bool Placed => HeadId is not null && Why is null;
}

/// <summary>
/// Phase 144 D1: the hub puts a group together itself. A head is a node that holds the model file and whose owner set
/// <c>AllowHead</c>; its members are the peers it can reach whose offers, together with its own, hold the model at the
/// context — chosen so the sum of the head's round trips to them is smallest (every token goes to each member and back in
/// turn, so that sum, not bandwidth, sets the speed — 141), then fewest members, then those that hold the file themselves
/// (143: they load from their own disk). Pure over what the nodes reported.
/// </summary>
public static class ShardPlacement
{
    /// <summary>Above this many reachable peers, a head considers only the nearest this many (2^12 subsets at most).</summary>
    public const int MaxPool = 12;

    /// <summary>
    /// The memory a group of these offers needs to hold <paramref name="file"/> at <paramref name="context"/>: weights and KV are
    /// split in proportion to each offer (the head's tensor split, 136 D3), and every worker also keeps 133's working reserve.
    /// So each worker's use is <c>(W + K) · offer / Σ</c> + reserve, which fits all of them exactly when
    /// <c>(W + K) ≤ Σ · (1 − reserve / smallest offer)</c>.
    /// </summary>
    public static (long WeightsMiB, long KvMiB, long ReserveMiB) Need(ShardModelFile file, int context)
    {
        context = Math.Clamp(context, 0, FitEstimator.MaxAskedContext);
        var kv = file.KvBytesPerToken is { } perToken ? (long)Math.Ceiling(perToken * (double)context / (1024 * 1024)) : 0;
        return (file.MiB, kv, 512 + context / 100);
    }

    public static bool Holds(long modelMiB, long reserveMiB, IReadOnlyList<long> offers)
    {
        if (offers.Count == 0)
        {
            return false;
        }

        var smallest = offers.Min();
        var total = offers.Sum();
        return smallest > reserveMiB && modelMiB * (double)smallest <= total * (double)(smallest - reserveMiB);
    }

    public static ShardPlacementResult Place(string model, int context, IReadOnlyList<PlacementCandidate> candidates)
    {
        var holders = candidates.Where(c => c.File is not null).ToArray();
        var heads = holders.Where(c => c.MayHead).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        if (heads.Length == 0)
        {
            return new ShardPlacementResult(null, [], holders.Length == 0
                ? $"no node that can take part now reports {model} in its group models directory (Node:Shards:ModelsDir); the head holds the file"
                : $"{string.Join(", ", holders.Select(h => h.Name))} hold{(holders.Length == 1 ? "s" : "")} {model}, and {(holders.Length == 1 ? "its owner has" : "their owners have")} not set Node:Shards:AllowHead", []);
        }

        var byId = candidates.ToDictionary(c => c.NodeId, StringComparer.OrdinalIgnoreCase);
        var waiting = new List<string>();
        var alone = new List<string>();
        var shortOf = new List<(string Name, long Have, long Need)>();
        (PlacementCandidate Head, PlacementCandidate[] Members, double Sum, long Need, List<string> Out)? best = null;

        foreach (var head in heads)
        {
            var (weights, kv, reserve) = Need(head.File!, context);
            var modelMiB = weights + kv;

            if (Holds(modelMiB, reserve, [head.OfferMiB]))
            {
                alone.Add($"{head.Name} offers {Gib(head.OfferMiB)} and holds it alone ({Gib(modelMiB)} + {Gib(reserve)} working space): serve it from {head.Name}'s own llama.cpp — a group would only be slower");
                continue;
            }

            var pool = new List<(PlacementCandidate Node, double Rtt)>();
            var left = new List<string>();
            var unmeasured = new List<string>();

            foreach (var peer in head.Peers)
            {
                if (!byId.TryGetValue(peer.NodeId, out var node) || node.NodeId == head.NodeId)
                {
                    continue;
                }

                if (node.Protocol != head.Protocol)
                {
                    left.Add($"{node.Name}: llama.cpp RPC v{node.Protocol}, {head.Name} v{head.Protocol}");
                }
                else if (peer.Error is { } error)
                {
                    left.Add($"{node.Name}: {head.Name} cannot reach it ({error})");
                }
                else if (peer.RttMs is not { } rtt)
                {
                    unmeasured.Add(node.Name);
                }
                else
                {
                    pool.Add((node, rtt));
                }
            }

            if (unmeasured.Count > 0)
            {
                // D3: a placement is kept once made, so it is made on a whole row of the matrix, never on half of it.
                waiting.Add($"{head.Name} has not measured its round trip to {string.Join(", ", unmeasured.Order(StringComparer.OrdinalIgnoreCase))} yet");
                continue;
            }

            var nearest = pool.OrderBy(p => p.Rtt).ThenBy(p => p.Node.Name, StringComparer.OrdinalIgnoreCase).ToArray();

            foreach (var far in nearest.Skip(MaxPool))
            {
                left.Add($"{far.Node.Name}: {Ms(far.Rtt)} from {head.Name}, past the {MaxPool} nearest");
            }

            nearest = nearest.Take(MaxPool).ToArray();
            (int Mask, double Sum, int Count, int Holding, string Key)? chosen = null;

            for (var mask = 1; mask < 1 << nearest.Length; mask++)
            {
                var offers = new List<long> { head.OfferMiB };
                var sum = 0d;
                var count = 0;
                var holding = 0;

                for (var i = 0; i < nearest.Length; i++)
                {
                    if ((mask & (1 << i)) == 0)
                    {
                        continue;
                    }

                    offers.Add(nearest[i].Node.OfferMiB);
                    sum += nearest[i].Rtt;
                    count++;
                    holding += nearest[i].Node.File?.Name == head.File!.Name ? 1 : 0;
                }

                if (!Holds(modelMiB, reserve, offers))
                {
                    continue;
                }

                // Round trips are compared to a tenth of a millisecond: a jitter below that is not a reason to prefer a group.
                var rounded = Math.Round(sum, 1);
                var key = string.Join(',', Enumerable.Range(0, nearest.Length).Where(i => (mask & (1 << i)) != 0).Select(i => nearest[i].Node.Name.ToLowerInvariant()));

                if (chosen is not { } c
                    || rounded < c.Sum
                    || (rounded == c.Sum && (count < c.Count
                        || (count == c.Count && (holding > c.Holding
                            || (holding == c.Holding && string.CompareOrdinal(key, c.Key) < 0))))))
                {
                    chosen = (mask, rounded, count, holding, key);
                }
            }

            if (chosen is not { } pick)
            {
                shortOf.Add((head.Name, head.OfferMiB + nearest.Sum(n => n.Node.OfferMiB), modelMiB + reserve * (nearest.Length + 1)));
                continue;
            }

            var members = Enumerable.Range(0, nearest.Length).Where(i => (pick.Mask & (1 << i)) != 0).Select(i => nearest[i]).ToArray();
            var outside = nearest.Where(n => !members.Contains(n)).Select(n => $"{n.Node.Name}: {Ms(n.Rtt)} from {head.Name}, not needed").Concat(left).ToList();

            if (best is not { } b
                || pick.Sum < b.Sum
                || (pick.Sum == b.Sum && members.Length < b.Members.Length))
            {
                best = (head, members.Select(m => m.Node).ToArray(), pick.Sum, modelMiB + reserve * (members.Length + 1), outside);
            }
        }

        if (best is not { } placed)
        {
            var why = waiting.Count > 0
                ? $"waiting for the latency matrix: {string.Join("; ", waiting)}"
                : shortOf.Count > 0
                    ? string.Join("; ", shortOf.Select(s => $"{s.Name} and every peer it can reach offer {Gib(s.Have)}, and {model} needs about {Gib(s.Need)} at {N(context)} tokens"))
                    : string.Join("; ", alone);
            return new ShardPlacementResult(null, [], why, alone.Concat(waiting).ToArray());
        }

        var head_ = placed.Head;
        var trip = head_.Peers.ToDictionary(p => p.NodeId, p => p.RttMs ?? 0, StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>
        {
            $"head {head_.Name}: holds {head_.File!.Name} ({Gib(head_.File.MiB)}), offers {Gib(head_.OfferMiB)}",
            $"members {string.Join(", ", placed.Members.Select(m => $"{m.Name} ({Ms(trip[m.NodeId])}, offers {Gib(m.OfferMiB)}{(m.File?.Name == head_.File.Name ? ", holds the file" : "")})"))}"
                + $": {Ms(placed.Members.Sum(m => trip[m.NodeId]))} of round trips per pass, about {Gib(placed.Need)} needed of {Gib(head_.OfferMiB + placed.Members.Sum(m => m.OfferMiB))} offered"
        };
        notes.AddRange(placed.Out.Select(o => "left out: " + o));
        notes.AddRange(alone);
        notes.AddRange(waiting.Select(w => "not considered as head: " + w));

        return new ShardPlacementResult(head_.NodeId, placed.Members.Select(m => m.NodeId).ToArray(), null, notes, Math.Round(placed.Members.Sum(m => trip[m.NodeId]), 2), placed.Need);
    }

    private static string Ms(double ms) => ms.ToString(ms < 10 ? "0.00" : "0", CultureInfo.InvariantCulture) + " ms";

    private static string Gib(long mib) => FitEstimator.Gib(mib);

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
