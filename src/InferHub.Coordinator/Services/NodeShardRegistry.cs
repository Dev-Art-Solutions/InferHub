using System.Collections.Concurrent;
using System.Net;
using InferHub.Coordinator.Hubs;
using InferHub.Shared.Fit;
using Microsoft.AspNetCore.SignalR;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about the slice it offers groups (phase 134, D5) — phase 45's registry once more. In
/// memory (rule 4): a coordinator restart forgets it and every node that offers one re-reports at registration. A node
/// that offers nothing never reports, so it has no row. Phase 135 adds where its hub connection came from, for a tunnel
/// that names no address of its own.
/// </summary>
public sealed class NodeShardRegistry
{
    private readonly ConcurrentDictionary<string, (NodeShardState State, IPAddress? From)> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeShardState state, IPAddress? from = null)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = (state, from);
        }
    }

    public NodeShardState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.TryGetValue(nodeId.Trim(), out var row) ? row.State : null;

    public IPAddress? From(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.TryGetValue(nodeId.Trim(), out var row) ? row.From : null;

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}

/// <summary>Phase 136 D1: <c>ShardGroups:&lt;name&gt;</c> — a group the admin defined: its head, its members (names or ids), the
/// model file on the head, the context.</summary>
public sealed class ShardGroupDefinition
{
    public string? Head { get; set; }

    public List<string> Members { get; set; } = [];

    /// <summary>Phase 137 D1: nodes that stand in, in order, for a member that cannot take part now — each one once, never the head.</summary>
    public List<string> Spares { get; set; } = [];

    public string? Model { get; set; }

    public int Context { get; set; } = 4096;

    /// <summary>Phase 138 D3: <c>private</c> (the default — members by name) or <c>community</c> (the coordinator picks <see cref="Workers"/>
    /// community nodes).</summary>
    public string Mode { get; set; } = "private";

    /// <summary>How many workers a community group takes besides its head.</summary>
    public int Workers { get; set; } = 2;

    public bool IsCommunity => string.Equals(Mode?.Trim(), "community", StringComparison.OrdinalIgnoreCase);

    /// <summary>Phase 144: <c>auto</c> — the coordinator picks the head (a node that holds <see cref="Model"/> and may head) and the
    /// members (its peers, by their offers and its round trips to them). <see cref="Head"/> pins the head; <see cref="Members"/>,
    /// when given, is the pool it picks from.</summary>
    public bool IsAuto => string.Equals(Mode?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Phase 144 D3: an <c>auto</c> group is placed only once the nodes it could use, and who may reach whom, have not
    /// changed for this long — so a fleet still connecting is not placed on its first two nodes and kept there.</summary>
    public int SettleSeconds { get; set; } = 30;

    /// <summary>Phase 145 D1/D2: how long a request for this group waits at the hub for it to run again — one that failed before its
    /// first token, or one that arrives while it reassembles — before the error or the 503 it gets today. 0 (the default): no wait.</summary>
    public int RetrySeconds { get; set; }

    /// <summary>Phase 145 D4: an <c>auto</c> group is placed again when a node its placement lost is back and has held still for
    /// <see cref="SettleSeconds"/>. Off: a kept placement moves only when it cannot run (144 D3).</summary>
    public bool FailBack { get; set; }

    /// <summary>Phase 146: a second file in the head's models directory that the head runs as a draft model on its own CPU —
    /// llama.cpp's speculative decoding, so one pass across the group checks several tokens. Unset (the default): no draft.
    /// Measured 1.38–1.44× at a 120 ms round trip and level on a local network (plan/phase-146); a head that lacks the file runs
    /// without it and says so.</summary>
    public string? Draft { get; set; }

    /// <summary>Phase 146: up to this many drafted tokens a pass (1–32). 0: the measured six.</summary>
    public int DraftTokens { get; set; }

    /// <summary>Phase 147: how many requests the head's <c>llama-server</c> runs at once, all in one pass across the group (<c>-np</c>,
    /// one context shared by all of them). 0 (the default): llama.cpp's own count, four at b12182. Whatever runs, the head reports the
    /// count its engine answered, and the hub's slot routing and slot wait (108/110) read that.</summary>
    public int Slots { get; set; }
}

/// <summary>The <c>ShardGroups</c> section, by group name. Empty: no group, and nothing is ever sent (the invariant).</summary>
public sealed class ShardGroupsOptions
{
    public const string SectionName = "ShardGroups";

    public Dictionary<string, ShardGroupDefinition> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One group as the hub sees it (D1): whether it is ready, why not, and what its head says.</summary>
public sealed record ShardGroupStatus(
    string Name,
    string? Head,
    IReadOnlyList<string> Members,
    string? Model,
    bool Ready,
    string? Why,
    ShardGroupState? Reported,
    IReadOnlyList<string>? Substitutions = null)
{
    public IReadOnlyList<string> Substitutions { get; init; } = Substitutions ?? [];

    /// <summary>Phase 143 D1: where each member's layers come from on a load — its own disk, or the head over the network, and why.</summary>
    public IReadOnlyList<string> Loads { get; init; } = [];

    /// <summary>Phase 144: for an <c>auto</c> group, how the coordinator placed it — the head, each member's round trip, what was left out.</summary>
    public IReadOnlyList<string> Placement { get; init; } = [];

    /// <summary>Phase 144: <c>private</c>, <c>community</c> or <c>auto</c> — whether a person or the coordinator chose the members.</summary>
    public string Mode { get; init; } = "private";
}

/// <summary>
/// Phase 135 D1: the coordinator decides which nodes' tunnels may reach each other. A node is a peer while it is connected
/// here, its worker runs, its tunnel listens, and it holds an active certificate this coordinator issued (127's record — the
/// thumbprint both ends check); peers share an organisation (130). Each node is sent its list only when it changed, and an
/// empty one when it stops being anyone's peer, so a revoked or departed node's doors close at once.
/// </summary>
public sealed class ShardPeerPublisher(
    INodeRegistry registry,
    NodeShardRegistry shards,
    IHubContext<NodeHub> hub,
    ILogger<ShardPeerPublisher> logger,
    InferHub.Coordinator.Auth.NodeCertificateAuthority? authority = null,
    Microsoft.Extensions.Options.IOptions<ShardGroupsOptions>? groupOptions = null,
    ShardRelay? relay = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile IReadOnlyDictionary<string, HashSet<string>> reach = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phase 139 D3: whether the last lists sent make <paramref name="to"/> one of <paramref name="from"/>'s peers — what the relay asks before it joins two nodes.</summary>
    public bool MayReach(string from, string to) => reach.TryGetValue(from, out var peers) && peers.Contains(to);
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sentGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sentSeeds = new(StringComparer.Ordinal);

    private readonly object placing = new();
    private readonly Dictionary<string, Held> held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Shape, DateTimeOffset Since)> settling = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> brokeWhy = new(StringComparer.OrdinalIgnoreCase);
    private int settleTimer;

    // Phase 145 D3: per "group|slot", the spare standing in while the group is sent, and since when its member can take part again.
    private readonly ConcurrentDictionary<string, string> standIns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> backSince = new(StringComparer.OrdinalIgnoreCase);

    // Phase 145 D4: the nodes an auto group's placement lost, and since when each is back.
    private readonly Dictionary<string, HashSet<string>> lost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lostBackSince = new(StringComparer.OrdinalIgnoreCase);

    // Phase 145 D1: whether each group served at the last look, and how often it stopped serving — what a retry waits to see.
    private readonly ConcurrentDictionary<string, bool> serving = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> interruptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phase 144 D3: an <c>auto</c> group's placement, kept (in memory, rule 4) for as long as it can run.</summary>
    private sealed record Held(string Model, int Context, string HeadId, IReadOnlyList<string> MemberIds, IReadOnlyList<string> Notes);

    private IReadOnlyDictionary<string, ShardGroupDefinition> Definitions =>
        groupOptions?.Value.Groups ?? new Dictionary<string, ShardGroupDefinition>();

    /// <summary>
    /// Phase 136 D1: each configured group, ready or not and why, over the peer lists just computed. Ready = the head and
    /// every member connected, their workers running and tunnels up, on one RPC protocol, and every member in the head's
    /// own list — then the head is sent the group, each worker weighted by its offer (134 D4).
    /// </summary>
    public (IReadOnlyDictionary<string, ShardGroupList> Lists, IReadOnlyList<ShardGroupStatus> Status) ComputeGroups(IReadOnlyDictionary<string, ShardPeerList> peerLists)
    {
        var all = ComputeAll(peerLists);
        return (all.Lists, all.Status);
    }

    /// <summary>
    /// <see cref="ComputeGroups"/>, and since 143 each member's assignment — the layers it will hold, so it can fill its cache
    /// from its own copy of the file before the head loads (D1).
    /// </summary>
    internal (IReadOnlyDictionary<string, ShardGroupList> Lists, IReadOnlyList<ShardGroupStatus> Status, IReadOnlyDictionary<string, ShardSeedList> Seeds) ComputeAll(IReadOnlyDictionary<string, ShardPeerList> peerLists)
    {
        var seeds = new Dictionary<string, List<ShardSeedSpec>>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var nodes = registry.Snapshot(now);
        var lists = new Dictionary<string, List<ShardGroupSpec>>(StringComparer.Ordinal);
        var status = new List<ShardGroupStatus>();

        Services.NodeSnapshot? Find(string? key) => string.IsNullOrWhiteSpace(key)
            ? null
            : nodes.FirstOrDefault(n => string.Equals(n.NodeId, key.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? nodes.FirstOrDefault(n => string.Equals(n.Name, key.Trim(), StringComparison.OrdinalIgnoreCase));

        foreach (var (name, definition) in Definitions.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Phase 144: an auto group's head and members come from the placement, not from the definition.
            var auto = definition.IsAuto;
            var placed = auto && !string.IsNullOrWhiteSpace(definition.Model) ? Place(name, definition, nodes, peerLists) : default;
            var head = auto ? placed.Head : Find(definition.Head);
            var reported = head is null ? null : shards.Of(head.NodeId)?.Groups?.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            var listed = head is not null && peerLists.TryGetValue(head.ConnectionId, out var headList)
                ? headList.Peers.Select(p => p.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var protocol = head is null ? null : shards.Of(head.NodeId)?.Protocol;

            // D1 (137): why one node cannot take part right now — or null when it can. The head's own checks are the same,
            // less "a peer of the head".
            string? NotReady(string key, Services.NodeSnapshot? node, bool isHead)
            {
                if (node is null)
                {
                    return isHead ? $"the head '{key}' is not connected" : $"member '{key}' is not connected";
                }

                if (Unavailable(node) is { } unavailable)
                {
                    return unavailable;
                }

                var state = shards.Of(node.NodeId)!;

                if (!isHead && !listed.Contains(node.NodeId))
                {
                    return $"{node.Name} is not one of {head!.Name}'s peers (a certificate, the same organisation — 135)";
                }

                return !isHead && state.Protocol != protocol
                    ? $"{node.Name} runs llama.cpp RPC v{state.Protocol}, the head v{protocol}"
                    : null;
            }

            string? why = string.IsNullOrWhiteSpace(definition.Model) ? "no Model: the file on the head to run"
                : auto ? placed.Why ?? NotReady(head?.Name ?? "", head, isHead: true)
                : NotReady(definition.Head ?? "", head, isHead: true);
            var members = new List<(string Slot, Services.NodeSnapshot Node)>();
            var substitutions = new List<string>();

            var community = definition.IsCommunity;

            if (why is null && community)
            {
                // Phase 138 D3: the head runs it and sees every prompt, so it opted in too; the members are picked here.
                if (shards.Of(head!.NodeId)?.Community != true || head.Sealed == true)
                {
                    why = $"{head.Name} has not joined community groups (Node:Shards:Community and CommunityAcceptRisk), so it cannot head one";
                }
                else
                {
                    var picked = nodes
                        .Where(n => n.NodeId != head.NodeId && shards.Of(n.NodeId)?.Community == true && n.Sealed != true && NotReady(n.Name, n, isHead: false) is null)
                        .OrderByDescending(n => Weight(shards.Of(n.NodeId)!))
                        .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, definition.Workers))
                        .ToArray();

                    if (picked.Length < Math.Max(1, definition.Workers))
                    {
                        why = $"only {picked.Length} community node(s) can take part now; the group wants {Math.Max(1, definition.Workers)}";
                    }

                    members.AddRange(picked.Select(n => (n.Name, n)));
                }
            }

            if (why is null && auto)
            {
                members.AddRange(placed.Members.Select(n => (n.Name, n)));
            }

            // Phase 145 D3: which spare stands in for which slot on this look — kept only if the group is sent.
            var standing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (why is null && !community && !auto)
            {
                var slots = definition.Members.Where(m => !string.Equals(Find(m)?.NodeId, head!.NodeId, StringComparison.OrdinalIgnoreCase)).ToArray();
                var spares = definition.Spares
                    .Select(s => (Key: s, Node: Find(s)))
                    .Where(s => s.Node is not null && s.Node.NodeId != head!.NodeId && slots.All(m => !string.Equals(Find(m)?.NodeId, s.Node.NodeId, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (slots.Length == 0)
                {
                    why = "no members besides the head: a group of one is the head's own llama.cpp";
                }

                foreach (var slot in slots)
                {
                    var node = Find(slot);
                    var slotKey = $"{name}|{slot}";
                    standIns.TryGetValue(slotKey, out var standingIn);
                    var keeping = standingIn is null ? default : spares.FirstOrDefault(s => s.Node!.NodeId == standingIn);
                    var keepingReady = keeping.Node is not null && NotReady(keeping.Key, keeping.Node, isHead: false) is null;

                    if (NotReady(slot, node, isHead: false) is not { } problem)
                    {
                        // D3 (145): back while its spare serves — the spare keeps the slot until the member has held still for
                        // SettleSeconds, so a member that drops and returns costs one reload, not two.
                        if (keepingReady)
                        {
                            var back = backSince.GetOrAdd(slotKey, now);
                            var left = TimeSpan.FromSeconds(Math.Max(0, definition.SettleSeconds)) - (now - back);

                            if (left > TimeSpan.Zero)
                            {
                                Wake(left);
                                spares.Remove(keeping);
                                members.Add((slot, keeping.Node!));
                                standing[slotKey] = keeping.Node!.NodeId;
                                substitutions.Add($"{keeping.Node!.Name} stands in for {node!.Name}, which is back: it takes its place back in {(int)Math.Ceiling(left.TotalSeconds)} s");
                                continue;
                            }
                        }

                        backSince.TryRemove(slotKey, out _);
                        members.Add((slot, node!));
                        continue;
                    }

                    backSince.TryRemove(slotKey, out _);

                    // D1 (137): the first spare that can take part now stands in; each spare once. The one already standing in keeps
                    // the slot (145) — another would be a reload for nothing.
                    var stand = keepingReady ? keeping : spares.FirstOrDefault(s => NotReady(s.Key, s.Node, isHead: false) is null);

                    if (stand.Node is null)
                    {
                        why ??= definition.Spares.Count > 0 ? $"{problem}, and no spare can stand in" : problem;
                        continue;
                    }

                    spares.Remove(stand);
                    members.Add((slot, stand.Node));
                    standing[slotKey] = stand.Node.NodeId;
                    substitutions.Add($"{stand.Node.Name} stands in for {node?.Name ?? slot} ({problem})");
                }
            }

            var loads = new List<string>();

            if (why is null)
            {
                // Phase 143 D1: every member is told its place in the split first. One that can fill its cache (it reports
                // seeds) holds the group back until it has said whether it holds the file and, if so, until it is done; one
                // that cannot is loaded over the network, as before.
                var weights = new[] { Weight(shards.Of(head!.NodeId)!) }.Concat(members.Select(m => Weight(shards.Of(m.Node.NodeId)!))).ToArray();
                var model = definition.Model!.Trim();

                for (var i = 0; i < members.Count; i++)
                {
                    var member = members[i].Node;
                    var key = $"{name}|{model}|{i + 1}|{string.Join(',', weights)}";

                    if (shards.Of(member.NodeId)?.Seeds is not { } said)
                    {
                        loads.Add($"{member.Name}: over the network (its worker keeps no cache, or it is older than 3.108)");
                        continue;
                    }

                    if (!seeds.TryGetValue(member.ConnectionId, out var mine))
                    {
                        seeds[member.ConnectionId] = mine = [];
                    }

                    mine.Add(new ShardSeedSpec(key, name, model, weights, i + 1));
                    var seed = said.FirstOrDefault(r => r.Key == key);

                    switch (seed?.State)
                    {
                        case null:
                            why ??= $"waiting for {member.Name} to say whether it holds {model}";
                            loads.Add($"{member.Name}: asked whether it holds {model}");
                            break;
                        case ShardSeedState.Seeding:
                            var progress = seed.TotalMiB > 0 ? $" ({seed.DoneMiB} of {seed.TotalMiB} MiB)" : "";
                            why ??= $"{member.Name} is filling its cache from its own copy of {model}{progress}";
                            loads.Add($"{member.Name}: from its own disk, filling{progress}");
                            break;
                        case ShardSeedState.Ready:
                            loads.Add($"{member.Name}: from its own disk ({seed.TotalMiB} MiB in its cache)");
                            break;
                        default:
                            loads.Add($"{member.Name}: over the network ({seed.Error ?? seed.State})");
                            break;
                    }
                }
            }

            if (why is null)
            {
                if (!lists.TryGetValue(head!.ConnectionId, out var specs))
                {
                    lists[head.ConnectionId] = specs = [];
                }

                specs.Add(new ShardGroupSpec(
                    name,
                    definition.Model!.Trim(),
                    definition.Context,
                    Weight(shards.Of(head.NodeId)!),
                    members.Select(m => new ShardGroupWorker(m.Node.NodeId, m.Node.Name, Weight(shards.Of(m.Node.NodeId)!))).ToArray(),
                    community,
                    string.IsNullOrWhiteSpace(definition.Draft) ? null : definition.Draft.Trim(),
                    Math.Max(0, definition.DraftTokens),
                    Math.Max(0, definition.Slots)));
            }

            // Phase 145 D3: a stand-in is remembered only while the group is sent with it; a group not sent has nothing to reload.
            foreach (var key in standIns.Keys.Where(k => k.StartsWith(name + "|", StringComparison.OrdinalIgnoreCase) && (why is not null || !standing.ContainsKey(k))).ToArray())
            {
                standIns.TryRemove(key, out _);
                backSince.TryRemove(key, out _);
            }

            if (why is null)
            {
                foreach (var (key, spare) in standing)
                {
                    standIns[key] = spare;
                }
            }

            // Phase 145 D1: a group that served and now does not is an interruption — a request that failed on it waits for one.
            var servesNow = why is null && reported?.State == ShardGroupState.Running;

            if (serving.TryGetValue(name, out var served) && served && !servesNow)
            {
                interruptions.AddOrUpdate(name, 1, (_, n) => n + 1);
            }

            serving[name] = servesNow;

            status.Add(new ShardGroupStatus(name, head?.Name ?? (auto ? null : definition.Head),
                why is null || auto ? members.Select(m => m.Node.Name).ToArray() : definition.Members,
                definition.Model, why is null, why, reported, substitutions)
            {
                Loads = loads,
                Placement = placed.Notes ?? [],
                Mode = auto ? "auto" : community ? "community" : "private"
            });
        }

        var result = nodes.ToDictionary(n => n.ConnectionId, n => new ShardGroupList(lists.TryGetValue(n.ConnectionId, out var s) ? s : []), StringComparer.Ordinal);
        var seedLists = nodes.ToDictionary(n => n.ConnectionId, n => new ShardSeedList(seeds.TryGetValue(n.ConnectionId, out var s) ? s : []), StringComparer.Ordinal);
        return (result, status, seedLists);
    }

    /// <summary>
    /// D2 (137): why a configured group answers nobody right now — the sentence of a 503 — or null when the model is not a
    /// group's, or its head serves it.
    /// </summary>
    public string? WhyGroupIsNotServing(string model)
    {
        if (!Definitions.TryGetValue(model.Trim(), out _))
        {
            return null;
        }

        var status = GroupStatus().FirstOrDefault(s => string.Equals(s.Name, model.Trim(), StringComparison.OrdinalIgnoreCase));

        return status switch
        {
            null => null,
            { Ready: false } => $"group '{status.Name}' is not running: {status.Why}",
            { Reported.State: ShardGroupState.Running } => null,
            { Substitutions.Count: > 0 } => $"group '{status.Name}' is reassembling: {string.Join("; ", status.Substitutions)}",
            _ => $"group '{status.Name}' is starting on {status.Head}: {status.Reported?.State ?? "sent to the head"}"
        };
    }

    /// <summary>Phase 145 D1/D2: how long a request for this group may wait for it at the hub, or null when the model is no group's or
    /// the group does not wait.</summary>
    public TimeSpan? RetryWindow(string model) =>
        !string.IsNullOrWhiteSpace(model) && Definitions.TryGetValue(model.Trim(), out var definition) && definition.RetrySeconds > 0
            ? TimeSpan.FromSeconds(definition.RetrySeconds)
            : null;

    /// <summary>Phase 145 D1: how many times this group has stopped serving since the coordinator started.</summary>
    public long Interruptions(string model) => interruptions.GetValueOrDefault(model.Trim());

    private static long Weight(NodeShardState state) => Math.Max(1, state.RamMiB + state.VramMiB);

    private static bool Is(Services.NodeSnapshot node, string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && (string.Equals(node.NodeId, key.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(node.Name, key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a connected node cannot be in any group right now (137 D1), or null: cordoned, paused, no worker, no tunnel.</summary>
    private string? Unavailable(Services.NodeSnapshot node)
    {
        if (node.Cordoned)
        {
            return $"{node.Name} is cordoned";
        }

        if (node.Sharing is { Paused: true } paused)
        {
            return $"{node.Name} is paused by its owner ({paused.Reason ?? "not sharing now"})";
        }

        var state = shards.Of(node.NodeId);

        if (state is not { State: NodeShardState.Running })
        {
            return $"{node.Name}'s worker is not running (Node:Shards)";
        }

        return state.Tunnel is not { Off: null } ? $"{node.Name}'s tunnel is not open: {state.Tunnel?.Off ?? "no tunnel"}" : null;
    }

    /// <summary>
    /// Phase 144 D1/D3: an <c>auto</c> group's head and members. A placement once made is kept while every node in it can still
    /// take part, the head still holds the file and the offers still hold the model; only then is it made again, on the matrix of
    /// that moment, and the notes say what broke. A coordinator restart forgets it (rule 4) and places afresh.
    /// </summary>
    private (Services.NodeSnapshot? Head, IReadOnlyList<Services.NodeSnapshot> Members, string? Why, IReadOnlyList<string>? Notes) Place(
        string name, ShardGroupDefinition definition, IReadOnlyCollection<Services.NodeSnapshot> nodes, IReadOnlyDictionary<string, ShardPeerList> peerLists)
    {
        var model = definition.Model!.Trim();
        var pool = nodes.Where(n => definition.Members.Count == 0 || definition.Members.Any(m => Is(n, m)) || Is(n, definition.Head)).ToArray();

        HashSet<string> Listed(Services.NodeSnapshot node) => peerLists.TryGetValue(node.ConnectionId, out var list)
            ? list.Peers.Select(p => p.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (placing)
        {
            string? broke = null;

            if (held.TryGetValue(name, out var keep))
            {
                broke = keep.Model != model || keep.Context != definition.Context
                    ? "the group's model or context changed"
                    : Broken(keep, pool, Listed);

                // D4 (145): a node this group lost, back and settled, is a reason to place it again — only with FailBack.
                broke ??= definition.FailBack ? Returned(name, definition, pool) : null;

                if (broke is null)
                {
                    return (pool.First(n => n.NodeId == keep.HeadId), keep.MemberIds.Select(id => pool.First(n => n.NodeId == id)).ToArray(), null, keep.Notes);
                }

                held.Remove(name);
                brokeWhy[name] = broke;

                if (!lost.TryGetValue(name, out var gone))
                {
                    lost[name] = gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                gone.UnionWith(new[] { keep.HeadId }.Concat(keep.MemberIds).Where(id => pool.FirstOrDefault(n => n.NodeId == id) is not { } member || Unavailable(member) is not null));
            }

            // Found in the lab: the reason must outlive the settling that follows a break, or the new placement forgets it.
            broke ??= brokeWhy.GetValueOrDefault(name);

            var candidates = pool
                .Where(n => Unavailable(n) is null)
                .Select(n =>
                {
                    var state = shards.Of(n.NodeId)!;
                    var listed = Listed(n);
                    return new PlacementCandidate(
                        n.NodeId,
                        n.Name,
                        Weight(state),
                        state.Models?.FirstOrDefault(f => string.Equals(f.Name, model, StringComparison.Ordinal)),
                        state is { MayHead: true, Models: not null } && (string.IsNullOrWhiteSpace(definition.Head) || Is(n, definition.Head)),
                        state.Protocol,
                        (state.Peers ?? []).Where(p => listed.Contains(p.NodeId)).ToArray());
                })
                .ToArray();

            // D3: a placement is kept, so it is made only once the fleet holds still — who could take part, who holds the file,
            // who may reach whom — for SettleSeconds. Round trips are not part of it: a row still measuring waits in Place.
            var shape = string.Join('|', candidates
                .OrderBy(c => c.NodeId, StringComparer.Ordinal)
                .Select(c => $"{c.NodeId}:{c.OfferMiB}:{c.File?.MiB}:{c.MayHead}:{c.Protocol}:{string.Join(',', c.Peers.Select(p => p.NodeId).Order(StringComparer.Ordinal))}"));
            var now = DateTimeOffset.UtcNow;

            if (!settling.TryGetValue(name, out var since) || since.Shape != shape)
            {
                settling[name] = since = (shape, now);
            }

            var wait = TimeSpan.FromSeconds(Math.Max(0, definition.SettleSeconds)) - (now - since.Since);

            if (wait > TimeSpan.Zero)
            {
                Wake(wait);
                var settle = $"settling: the nodes it could use changed {(int)(now - since.Since).TotalSeconds} s ago; it is placed after {definition.SettleSeconds} s without a change";
                return (null, [], settle, broke is null ? [] : [$"placed again: {broke}"]);
            }

            var result = ShardPlacement.Place(model, definition.Context, candidates);
            IReadOnlyList<string> notes = broke is null ? result.Notes : [$"placed again: {broke}", .. result.Notes];

            if (!result.Placed)
            {
                return (null, [], result.Why, notes);
            }

            held[name] = new Held(model, definition.Context, result.HeadId!, result.MemberIds, notes);
            brokeWhy.Remove(name);

            if (lost.TryGetValue(name, out var stillLost))
            {
                stillLost.ExceptWith(new[] { result.HeadId! }.Concat(result.MemberIds));
            }
            logger.LogInformation("Placed group {Group}: {Notes}", name, string.Join("; ", notes));
            return (pool.First(n => n.NodeId == result.HeadId), result.MemberIds.Select(id => pool.First(n => n.NodeId == id)).ToArray(), null, notes);
        }
    }

    /// <summary>D3: look again once a settling group may be placed — nothing else would, if the fleet then stays quiet. One at a time.</summary>
    private void Wake(TimeSpan after)
    {
        if (Interlocked.Exchange(ref settleTimer, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(after + TimeSpan.FromSeconds(1));
                Interlocked.Exchange(ref settleTimer, 0);
                await PublishAsync();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref settleTimer, 0);
                logger.LogDebug(ex, "Could not look at the settling shard groups again");
            }
        });
    }

    /// <summary>
    /// Phase 145 D4: "shard-d is back" once a node the placement lost can take part again and has for <c>SettleSeconds</c> — the
    /// reason to place the group again — or null. Each return is a reason once. Called under <c>placing</c>.
    /// </summary>
    private string? Returned(string name, ShardGroupDefinition definition, IReadOnlyList<Services.NodeSnapshot> pool)
    {
        if (!lost.TryGetValue(name, out var gone) || gone.Count == 0)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var id in gone.ToArray())
        {
            var key = $"{name}|{id}";

            if (pool.FirstOrDefault(n => n.NodeId == id) is not { } node || Unavailable(node) is not null)
            {
                lostBackSince.Remove(key);
                continue;
            }

            if (!lostBackSince.TryGetValue(key, out var since))
            {
                lostBackSince[key] = since = now;
            }

            var left = TimeSpan.FromSeconds(Math.Max(0, definition.SettleSeconds)) - (now - since);

            if (left > TimeSpan.Zero)
            {
                Wake(left);
                continue;
            }

            gone.Remove(id);
            lostBackSince.Remove(key);
            return $"{node.Name} is back";
        }

        return null;
    }

    /// <summary>D3: what stops a kept placement from running now, or null when it still can.</summary>
    private string? Broken(Held keep, IReadOnlyList<Services.NodeSnapshot> pool, Func<Services.NodeSnapshot, HashSet<string>> listed)
    {
        var head = pool.FirstOrDefault(n => n.NodeId == keep.HeadId);

        if (head is null)
        {
            return "its head left";
        }

        if (Unavailable(head) is { } problem)
        {
            return problem;
        }

        var state = shards.Of(head.NodeId)!;
        var file = state.Models?.FirstOrDefault(f => string.Equals(f.Name, keep.Model, StringComparison.Ordinal));

        if (file is null || state.MayHead != true)
        {
            return $"{head.Name} no longer holds {keep.Model} or may no longer head";
        }

        var reach = listed(head);
        var offers = new List<long> { Weight(state) };

        foreach (var id in keep.MemberIds)
        {
            var member = pool.FirstOrDefault(n => n.NodeId == id);

            if (member is null)
            {
                return "a member left";
            }

            if (Unavailable(member) is { } memberProblem)
            {
                return memberProblem;
            }

            var memberState = shards.Of(member.NodeId)!;

            if (!reach.Contains(member.NodeId) || memberState.Protocol != state.Protocol)
            {
                return $"{member.Name} is no longer one of {head.Name}'s peers on its protocol";
            }

            offers.Add(Weight(memberState));
        }

        var (weights, kv, reserve) = ShardPlacement.Need(file, keep.Context);
        return ShardPlacement.Holds(weights + kv, reserve, offers) ? null : "the offers no longer hold the model";
    }

    /// <summary>Phase 144 D3: forget an <c>auto</c> group's placement, so the next look places it on the matrix of now. False: none was held.</summary>
    public bool Replace(string name)
    {
        lock (placing)
        {
            if (!held.Remove(name.Trim()))
            {
                return false;
            }

            brokeWhy[name.Trim()] = "an admin asked (POST /api/admin/shards/groups/{name}/replace)";
            return true;
        }
    }

    /// <summary>
    /// Phase 137 D1: with groups configured, any change in the registry — an owner's pause (116), a cordon — is a reason to
    /// look again, not only a shard report or a disconnect. Lists are still sent only when they change.
    /// </summary>
    public void Watch()
    {
        if (Definitions.Count == 0)
        {
            return;
        }

        registry.Changed += () => _ = Task.Run(async () =>
        {
            try
            {
                await PublishAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not republish shard groups after a registry change");
            }
        });
    }

    /// <summary>Phase 138 D4: whether a model is a community group's — listed as <c>inferhub-community</c>, sent only with the client's acceptance.</summary>
    public bool IsCommunityGroup(string model) =>
        !string.IsNullOrWhiteSpace(model) && Definitions.TryGetValue(model.Trim(), out var definition) && definition.IsCommunity;

    /// <summary>For <c>GET /api/admin/shards/groups</c>.</summary>
    public IReadOnlyList<ShardGroupStatus> GroupStatus() => ComputeGroups(Compute()).Status;

    /// <summary>Who may reach whom right now: per connection id, the list it should hold. Pure over the registries.</summary>
    public IReadOnlyDictionary<string, ShardPeerList> Compute()
    {
        var now = DateTimeOffset.UtcNow;
        var active = (authority is { Enabled: true } ? authority.Certificates() : [])
            .Where(c => c.State(now) == "active")
            .GroupBy(c => c.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.IssuedUtc).First(), StringComparer.OrdinalIgnoreCase);

        var eligible = new List<(Services.NodeSnapshot Node, ShardPeer Peer, string? Organisation, bool Community)>();

        foreach (var node in registry.Snapshot(now))
        {
            if (shards.Of(node.NodeId) is not { State: NodeShardState.Running, Tunnel: { Off: null } tunnel }
                || !active.TryGetValue(node.NodeId, out var certificate)
                || Endpoint(tunnel, shards.From(node.NodeId)) is not { } endpoint)
            {
                continue;
            }

            // Phase 138 D1: a community node is one whose owner opted in and accepted the risk, and never a sealed one.
            var community = shards.Of(node.NodeId)?.Community == true && node.Sealed != true;
            // Phase 139 D1: it waits at this hub's relay only if the relay is on and its owner said so.
            var relayed = relay is { Enabled: true } && tunnel.Relay == true;
            eligible.Add((node, new ShardPeer(node.NodeId, node.Name, endpoint, certificate.Thumbprint, relayed), certificate.Organisation, community));
        }

        var lists = new Dictionary<string, ShardPeerList>(StringComparer.Ordinal);

        foreach (var node in registry.Snapshot(now))
        {
            var me = eligible.FirstOrDefault(e => e.Node.ConnectionId == node.ConnectionId);
            lists[node.ConnectionId] = new ShardPeerList(me.Node is null
                ? []
                : eligible
                    // Phase 138 D2: one organisation, or both opted into community (across organisations).
                    .Where(e => e.Node.ConnectionId != node.ConnectionId && (string.Equals(e.Organisation, me.Organisation, StringComparison.Ordinal) || (e.Community && me.Community)))
                    .Select(e => e.Peer)
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                me.Node is not null && me.Peer.Relay ? new ShardRelayInfo(relay!.Host, relay.Port, me.Node.NodeId, relay.KeyFor(me.Node.NodeId)) : null);
        }

        return lists;
    }

    /// <summary>Sends each node its list when it differs from the last one it was sent.</summary>
    public async Task PublishAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var lists = Compute();

            if (relay is { Enabled: true })
            {
                // Phase 139 D3: the relay joins only nodes these lists make peers; a node that left them loses its place there now.
                var ids = registry.Snapshot(DateTimeOffset.UtcNow).ToDictionary(n => n.ConnectionId, n => n.NodeId, StringComparer.Ordinal);
                reach = lists.Where(l => ids.ContainsKey(l.Key)).ToDictionary(
                    l => ids[l.Key],
                    l => l.Value.Peers.Select(p => p.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
                relay.Authorize ??= MayReach;
                relay.Keep(lists.Where(l => l.Value.Relay is not null && ids.ContainsKey(l.Key)).Select(l => ids[l.Key]).ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            foreach (var gone in sent.Keys.Except(lists.Keys).ToArray())
            {
                sent.Remove(gone);
            }

            foreach (var (connection, list) in lists)
            {
                var key = string.Join('|', list.Peers.Select(p => $"{p.NodeId}@{p.Endpoint}#{p.Thumbprint}{(p.Relay ? "~" : "")}"))
                    + (list.Relay is { } r ? $"|relay:{r.Host}:{r.Port}:{r.Key}" : "");

                if (sent.TryGetValue(connection, out var last) ? last == key : list.Peers.Count == 0 && list.Relay is null)
                {
                    continue;
                }

                try
                {
                    await hub.Clients.Client(connection).SendAsync("ShardPeers", list, cancellationToken);
                    sent[connection] = key;
                    logger.LogInformation("Shard peers for connection {Connection}: {Peers}", connection, list.Peers.Count == 0 ? "none" : string.Join(", ", list.Peers.Select(p => $"{p.Name} ({p.Endpoint}{(p.Relay ? ", or the relay" : "")})")));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not send shard peers to connection {Connection}", connection);
                }
            }

            // Phase 136 D1: the groups each head should run, sent the same way — on change, an empty list to stop them.
            if (Definitions.Count == 0 && sentGroups.Count == 0)
            {
                return;
            }

            var (groupLists, status, seedLists) = ComputeAll(lists);

            // Phase 143 D1: each member's assignments, before the head's groups — sent the same way, on change.
            foreach (var gone in sentSeeds.Keys.Except(seedLists.Keys).ToArray())
            {
                sentSeeds.Remove(gone);
            }

            foreach (var (connection, list) in seedLists)
            {
                var key = string.Join('|', list.Seeds.Select(s => s.Key));

                if (sentSeeds.TryGetValue(connection, out var last) ? last == key : list.Seeds.Count == 0)
                {
                    continue;
                }

                try
                {
                    await hub.Clients.Client(connection).SendAsync("ShardSeeds", list, cancellationToken);
                    sentSeeds[connection] = key;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not send shard seeds to connection {Connection}", connection);
                }
            }

            foreach (var gone in sentGroups.Keys.Except(groupLists.Keys).ToArray())
            {
                sentGroups.Remove(gone);
            }

            foreach (var (connection, list) in groupLists)
            {
                var key = string.Join('|', list.Groups.Select(g => $"{g.Name}:{g.Model}:{g.Context}:{g.HeadWeight}:{string.Join(',', g.Members.Select(m => $"{m.NodeId}={m.Weight}"))}"));

                if (sentGroups.TryGetValue(connection, out var last) ? last == key : list.Groups.Count == 0)
                {
                    continue;
                }

                try
                {
                    await hub.Clients.Client(connection).SendAsync("ShardGroups", list, cancellationToken);
                    sentGroups[connection] = key;
                    logger.LogInformation("Shard groups for connection {Connection}: {Groups}", connection, list.Groups.Count == 0 ? "none" : string.Join(", ", list.Groups.Select(g => $"{g.Name} ({g.Model}, {g.Members.Count + 1} workers)")));
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not send shard groups to connection {Connection}", connection);
                }
            }

            foreach (var group in status.Where(s => !s.Ready))
            {
                logger.LogDebug("Shard group {Group} not ready: {Why}", group.Name, group.Why);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>D4: the node's own <c>TunnelAdvertise</c>, else the address its hub connection came from and its port.</summary>
    public static string? Endpoint(ShardTunnelState tunnel, IPAddress? from)
    {
        if (!string.IsNullOrWhiteSpace(tunnel.Advertise))
        {
            return tunnel.Advertise.Trim();
        }

        if (from is null)
        {
            return null;
        }

        var address = from.IsIPv4MappedToIPv6 ? from.MapToIPv4() : from;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]:{tunnel.Port}"
            : $"{address}:{tunnel.Port}";
    }
}
