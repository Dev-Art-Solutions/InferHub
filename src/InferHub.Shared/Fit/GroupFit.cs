using System.Globalization;

namespace InferHub.Shared.Fit;

/// <summary>A node a group could use, with the facts it gave about itself (phase 133, D1).</summary>
public sealed record GroupCandidate(string NodeId, string Name, MachineFacts Machine);

/// <summary>One member of a group: which layers it would hold and what that costs it.</summary>
/// <param name="Role"><see cref="GroupFit.Head"/> (holds the file, runs llama-server) or <see cref="GroupFit.Worker"/>.</param>
/// <param name="FirstLayer">The first of its layers, counted from 0; members hold consecutive runs, head first.</param>
/// <param name="Device"><c>card+ram</c> (its card first, the rest of its layers in RAM on its CPU) or <c>ram</c> (no card);
/// prefixed <c>offer:</c> when the member is counted by what its owner offered groups (134), not its whole memory.</param>
public sealed record GroupMember(string NodeId, string Name, string Role, int FirstLayer, int Layers, long MemoryMiB, long CapacityMiB, string Device);

/// <param name="Alone">A node that holds the model by itself — then no group is needed, and that is the answer.</param>
/// <param name="NeedMiB">Weights + KV at the context + each member's working reserve.</param>
/// <param name="HaveMiB">What the members (or, when it does not fit, every candidate) offer together.</param>
public sealed record GroupPlan(
    string Variant,
    bool Fits,
    string? Alone,
    IReadOnlyList<GroupMember> Members,
    long WeightsMiB,
    long KvMiB,
    long NeedMiB,
    long HaveMiB,
    int Layers,
    IReadOnlyList<string> Why);

/// <summary>
/// Phase 133: would this model fit across several nodes, split by layers the way llama.cpp's RPC backend splits it —
/// pure arithmetic over the model's facts and each node's own <see cref="MachineFacts"/>, by 118's rules for what a
/// machine can give (<see cref="FitEstimator.Vram"/>, <see cref="FitEstimator.Ram"/>). Conservative like 118: the KV
/// cache is spread over every layer and each member pays the whole working reserve.
/// </summary>
public static class GroupFit
{
    public const string Head = "head";
    public const string Worker = "worker";

    private const double Mi = 1024 * 1024;

    /// <summary>Why this model cannot be split across nodes at all, or null when it can.</summary>
    public static string? CanSplit(ModelFacts model) => model.Source switch
    {
        ModelFacts.SourceGguf when model.Layers is > 1 => null,
        ModelFacts.SourceGguf => "the model's header names no layer count, so there is nothing to split by",
        ModelFacts.SourceOllama => "a group runs llama.cpp over a GGUF from Hugging Face; link the GGUF repository of this model",
        ModelFacts.SourceCheckpoint => "a group runs llama.cpp, which needs a GGUF; link a GGUF repository of the same model",
        _ => "a group runs llama.cpp over a GGUF; this source is not one"
    };

    /// <summary>A plan per variant: those that fit one node first, then groups by fewest members, then those that fit nowhere.</summary>
    public static IReadOnlyList<GroupPlan> PlanAll(ModelFacts model, IReadOnlyList<GroupCandidate> candidates, int context) =>
        CanSplit(model) is not null
            ? []
            : model.Variants
                .Select(v => Plan(model, v, candidates, context))
                .OrderByDescending(p => p.Fits)
                .ThenByDescending(p => p.Alone is not null)
                .ThenBy(p => p.Fits ? p.Members.Count : int.MaxValue)
                .ThenByDescending(p => p.Fits ? p.WeightsMiB : -p.NeedMiB)
                .ToArray();

    public static GroupPlan Plan(ModelFacts model, ModelVariant variant, IReadOnlyList<GroupCandidate> candidates, int context)
    {
        context = Math.Clamp(context, 0, FitEstimator.MaxAskedContext);
        var layers = model.Layers ?? 1;
        var weights = Mib(variant.Bytes);
        var kv = model.KvBytesPerToken is { } perToken ? (long)Math.Ceiling(perToken * (double)context / Mi) : 0;
        var reserve = 512 + context / 100;
        var perLayer = (long)Math.Ceiling((weights + kv) / (double)layers);
        var why = new List<string>();

        var offered = candidates
            .Select(c => (Candidate: c, Capacity: Capacity(c.Machine)))
            .OrderByDescending(c => c.Capacity.MiB)
            .ThenBy(c => c.Candidate.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var everything = offered.Sum(c => c.Capacity.MiB);

        // Alone is 118's own answer for llama.cpp on that node (card and RAM together) — one opinion, not a second (D2).
        var single = model with { Variants = [variant] };
        var own = offered.ToDictionary(
            c => c.Candidate.NodeId,
            c => FitEstimator.Estimate(single, c.Candidate.Machine, context).FirstOrDefault(o => o.Engine == FitEngines.LlamaCpp),
            StringComparer.OrdinalIgnoreCase);
        var alone = offered
            .Select(c => (c.Candidate, Option: own[c.Candidate.NodeId] is { Fits: true } fits ? fits : null))
            .Where(a => a.Option is not null)
            .OrderByDescending(a => a.Option!.Score)
            .FirstOrDefault();

        if (alone.Option is { } option)
        {
            var device = option.VramMiB > 0 ? (option.RamMiB > 0 ? "card+ram" : "card") : "ram";
            why.Add($"{alone.Candidate.Name} holds it alone ({option.Placement}: {Gib(option.VramMiB)} on the card, {Gib(option.RamMiB)} in RAM — its own estimate) — no group is needed, and one node is faster than any group");
            return new GroupPlan(variant.Name, true, alone.Candidate.NodeId,
                [new GroupMember(alone.Candidate.NodeId, alone.Candidate.Name, Head, 0, layers, option.VramMiB + option.RamMiB, option.VramMiB + option.RamMiB, device)],
                weights, kv, option.VramMiB + option.RamMiB, option.VramMiB + option.RamMiB, layers, why);
        }

        // Phase 134 D3: llama.cpp checks the RPC protocol when a head connects, so a group is one protocol — the one with
        // the most memory offered. A node that announced none yet (no worker, or before 3.94) is not filtered.
        var protocol = offered
            .Where(c => c.Candidate.Machine.Shards?.Protocol is not null)
            .GroupBy(c => c.Candidate.Machine.Shards!.Protocol!)
            .OrderByDescending(g => g.Sum(c => c.Capacity.MiB))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key;
        var other = offered.Where(c => c.Candidate.Machine.Shards?.Protocol is { } p && p != protocol).ToArray();

        if (other.Length > 0)
        {
            why.Add($"left out — another llama.cpp RPC protocol than the group's v{protocol}: {string.Join(", ", other.Select(c => $"{c.Candidate.Name} (v{c.Candidate.Machine.Shards!.Protocol})"))}; a head refuses a worker on another version");
        }

        // Largest first, until the members' whole layers cover the model (D2).
        var taken = new List<(GroupCandidate Candidate, (long MiB, string Device) Capacity, int Max)>();
        var room = 0;

        foreach (var (candidate, capacity) in offered.Except(other))
        {
            var max = (int)Math.Max(0, (capacity.MiB - reserve) / Math.Max(1, perLayer));

            if (max == 0)
            {
                continue;
            }

            taken.Add((candidate, capacity, max));
            room += max;

            if (room >= layers)
            {
                break;
            }
        }

        if (room < layers)
        {
            var short_ = layers - room;
            why.Add($"needs {layers} layers of about {Gib(perLayer)} each ({Gib(weights)} of weights + {Gib(kv)} of KV for {N(context)} tokens); every node asked together holds {room} — {short_} short, about {Gib(short_ * perLayer)} more");
            return new GroupPlan(variant.Name, false, null, [], weights, kv, weights + kv + reserve * Math.Max(1, taken.Count), everything, layers, why);
        }

        if (taken.Count == 1)
        {
            // One node has the memory for every layer, and its own estimate still says no (its disk, usually): a group of
            // one is that node, so its reason is the answer.
            var one = taken[0].Candidate;
            var reason = own[one.NodeId]?.Why.FirstOrDefault() ?? "its own estimate says it does not fit";
            why.Add($"{one.Name} alone has the memory for all {layers} layers, and its own estimate says no: {reason}");
            return new GroupPlan(variant.Name, false, null, [], weights, kv, weights + kv + reserve, taken[0].Capacity.MiB, layers, why);
        }

        var split = Spread(layers, taken.Select(t => (t.Capacity.MiB - reserve, t.Max)).ToArray());
        var members = taken.Zip(split).Where(m => m.Second > 0).Select(m => m.First).ToList();
        var counts = split.Where(s => s > 0).ToArray();

        // The head sends the weights to the others, so it is the one that keeps the file (RPC loads from the head).
        var headIndex = members.FindIndex(m => Disk(m.Candidate.Machine) is not { } free || free >= weights);
        var notes = new List<string>();

        if (headIndex < 0)
        {
            headIndex = 0;
            notes.Add($"no member has {Gib(weights)} of free disk for the file where llama.cpp keeps models; the head needs it");
        }

        var order = Enumerable.Range(0, members.Count).OrderBy(i => i == headIndex ? 0 : 1).ToArray();
        var placed = new List<GroupMember>();
        var first = 0;

        foreach (var i in order)
        {
            var m = members[i];
            placed.Add(new GroupMember(m.Candidate.NodeId, m.Candidate.Name, i == headIndex ? Head : Worker, first, counts[i],
                counts[i] * perLayer + reserve, m.Capacity.MiB, m.Capacity.Device));
            first += counts[i];
        }

        var need = weights + kv + reserve * placed.Count;
        why.Add($"{placed.Count} nodes hold its {layers} layers, about {Gib(perLayer)} each: {Gib(weights)} of weights + {Gib(kv)} of KV for {N(context)} tokens, + {Gib(reserve)} working space on every member");
        why.Add($"every token goes to each worker and back in turn, several round trips each: the round trip between the nodes sets the speed, not the bandwidth — fewer, larger members are faster");
        var whole = placed.Where(p => !p.Device.StartsWith(OfferPrefix, StringComparison.Ordinal)).Select(p => p.Name).ToArray();

        if (whole.Length > 0)
        {
            // Phase 134 D4: a node that set nothing aside is counted as 133 counted it, and the plan says so.
            why.Add($"{string.Join(", ", whole)} offer{(whole.Length == 1 ? "s" : "")} nothing to groups yet (Node:Shards): {(whole.Length == 1 ? "its" : "their")} whole usable memory for llama.cpp is counted, card first and then RAM");
        }

        if (placed.Count(p => p.Device.StartsWith(OfferPrefix, StringComparison.Ordinal)) is var offering and > 0)
        {
            why.Add($"{offering} member{(offering == 1 ? "" : "s")} counted by what {(offering == 1 ? "its owner" : "their owners")} offered groups, not {(offering == 1 ? "its" : "their")} whole memory");
        }

        foreach (var member in placed)
        {
            var machine = candidates.First(c => c.NodeId == member.NodeId).Machine;
            var card = Card(machine);

            if (card == 0)
            {
                // Found live: a node with a card that offered RAM only is not a node "with no card".
                why.Add(machine.Shards is { TotalMiB: > 0 } && machine.Gpus.Count > 0
                    ? $"{member.Name} offered groups RAM only: its {member.Layers} layers run on its CPU, and the group waits for it on every token"
                    : $"{member.Name} has no card: its {member.Layers} layers run on its CPU, and the group waits for it on every token");
            }
            else if (member.MemoryMiB > card)
            {
                var past = (int)Math.Min(member.Layers, Math.Ceiling((member.MemoryMiB - card) / (double)Math.Max(1, perLayer)));
                why.Add($"{member.Name}: about {past} of its {member.Layers} layers do not fit its card and run on its CPU");
            }
        }

        why.AddRange(notes);
        return new GroupPlan(variant.Name, notes.Count == 0, null, placed, weights, kv, need, placed.Sum(p => p.CapacityMiB), layers, why);
    }

    /// <summary>A member's <see cref="GroupMember.Device"/> starts with this when it is counted by its owner's offer (134).</summary>
    public const string OfferPrefix = "offer:";

    /// <summary>
    /// What a node would give a group: its owner's offer (<c>Node:Shards</c>, never more than it has — 134 D4), else 118's
    /// usable VRAM and RAM for llama.cpp with the owner's limits applied, as 133 counted it.
    /// </summary>
    public static (long MiB, string Device) Capacity(MachineFacts machine)
    {
        if (machine.Shards is { TotalMiB: > 0 } offer)
        {
            var (whole, _) = Capacity(machine with { Shards = null });
            return (Math.Min(offer.TotalMiB, whole), OfferPrefix + (offer.VramMiB > 0 ? "card+ram" : "ram"));
        }

        var engine = LlamaCpp(machine);
        var scratch = new List<string>();
        var vram = FitEstimator.Vram(machine, engine, scratch);
        var ram = FitEstimator.Ram(machine, engine, scratch);
        return (vram + ram, vram > 0 ? "card+ram" : "ram");
    }

    private static long Card(MachineFacts machine) =>
        machine.Shards is { TotalMiB: > 0 } offer
            ? offer.VramMiB
            : FitEstimator.Vram(machine, LlamaCpp(machine), []);

    private static EngineFacts LlamaCpp(MachineFacts machine) =>
        machine.Engines.FirstOrDefault(e => e.Kind == FitEngines.LlamaCpp)
        ?? new EngineFacts(FitEngines.LlamaCpp, null, false, null, null, null, null, null);

    /// <summary>Whole layers in proportion to what each member has, never past its own maximum (D2).</summary>
    public static int[] Spread(int layers, IReadOnlyList<(long Usable, int Max)> members)
    {
        var total = members.Sum(m => Math.Max(1, m.Usable));
        var counts = members.Select(m => Math.Min(m.Max, (int)(layers * (Math.Max(1, m.Usable) / (double)total)))).ToArray();
        var left = layers - counts.Sum();

        // What proportion left over goes to whoever has room, the largest first; members are already largest first.
        while (left > 0)
        {
            var moved = false;

            for (var i = 0; i < counts.Length && left > 0; i++)
            {
                if (counts[i] < members[i].Max)
                {
                    counts[i]++;
                    left--;
                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        return counts;
    }

    private static long? Disk(MachineFacts machine) =>
        machine.Engines.FirstOrDefault(e => e.Kind == FitEngines.LlamaCpp)?.DiskFreeMiB;

    private static long Mib(long bytes) => (long)Math.Ceiling(bytes / Mi);

    private static string Gib(long mib) => FitEstimator.Gib(mib);

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
