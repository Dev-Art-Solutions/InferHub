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
    Microsoft.Extensions.Options.IOptions<ShardGroupsOptions>? groupOptions = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> sentGroups = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, ShardGroupDefinition> Definitions =>
        groupOptions?.Value.Groups ?? new Dictionary<string, ShardGroupDefinition>();

    /// <summary>
    /// Phase 136 D1: each configured group, ready or not and why, over the peer lists just computed. Ready = the head and
    /// every member connected, their workers running and tunnels up, on one RPC protocol, and every member in the head's
    /// own list — then the head is sent the group, each worker weighted by its offer (134 D4).
    /// </summary>
    public (IReadOnlyDictionary<string, ShardGroupList> Lists, IReadOnlyList<ShardGroupStatus> Status) ComputeGroups(IReadOnlyDictionary<string, ShardPeerList> peerLists)
    {
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
            var head = Find(definition.Head);
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

                if (state.Tunnel is not { Off: null })
                {
                    return $"{node.Name}'s tunnel is not open: {state.Tunnel?.Off ?? "no tunnel"}";
                }

                if (!isHead && !listed.Contains(node.NodeId))
                {
                    return $"{node.Name} is not one of {head!.Name}'s peers (a certificate, the same organisation — 135)";
                }

                return !isHead && state.Protocol != protocol
                    ? $"{node.Name} runs llama.cpp RPC v{state.Protocol}, the head v{protocol}"
                    : null;
            }

            string? why = string.IsNullOrWhiteSpace(definition.Model) ? "no Model: the file on the head to run" : NotReady(definition.Head ?? "", head, isHead: true);
            var members = new List<(string Slot, Services.NodeSnapshot Node)>();
            var substitutions = new List<string>();

            if (why is null)
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

                    if (NotReady(slot, node, isHead: false) is not { } problem)
                    {
                        members.Add((slot, node!));
                        continue;
                    }

                    // D1: the first spare that can take part now stands in; each spare once.
                    var stand = spares.FirstOrDefault(s => NotReady(s.Key, s.Node, isHead: false) is null);

                    if (stand.Node is null)
                    {
                        why ??= definition.Spares.Count > 0 ? $"{problem}, and no spare can stand in" : problem;
                        continue;
                    }

                    spares.Remove(stand);
                    members.Add((slot, stand.Node));
                    substitutions.Add($"{stand.Node.Name} stands in for {node?.Name ?? slot} ({problem})");
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
                    members.Select(m => new ShardGroupWorker(m.Node.NodeId, m.Node.Name, Weight(shards.Of(m.Node.NodeId)!))).ToArray()));
            }

            status.Add(new ShardGroupStatus(name, head?.Name ?? definition.Head, why is null ? members.Select(m => m.Node.Name).ToArray() : definition.Members,
                definition.Model, why is null, why, reported, substitutions));
        }

        var result = nodes.ToDictionary(n => n.ConnectionId, n => new ShardGroupList(lists.TryGetValue(n.ConnectionId, out var s) ? s : []), StringComparer.Ordinal);
        return (result, status);
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

    private static long Weight(NodeShardState state) => Math.Max(1, state.RamMiB + state.VramMiB);

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

        var eligible = new List<(Services.NodeSnapshot Node, ShardPeer Peer, string? Organisation)>();

        foreach (var node in registry.Snapshot(now))
        {
            if (shards.Of(node.NodeId) is not { State: NodeShardState.Running, Tunnel: { Off: null } tunnel }
                || !active.TryGetValue(node.NodeId, out var certificate)
                || Endpoint(tunnel, shards.From(node.NodeId)) is not { } endpoint)
            {
                continue;
            }

            eligible.Add((node, new ShardPeer(node.NodeId, node.Name, endpoint, certificate.Thumbprint), certificate.Organisation));
        }

        var lists = new Dictionary<string, ShardPeerList>(StringComparer.Ordinal);

        foreach (var node in registry.Snapshot(now))
        {
            var me = eligible.FirstOrDefault(e => e.Node.ConnectionId == node.ConnectionId);
            lists[node.ConnectionId] = new ShardPeerList(me.Node is null
                ? []
                : eligible
                    .Where(e => e.Node.ConnectionId != node.ConnectionId && string.Equals(e.Organisation, me.Organisation, StringComparison.Ordinal))
                    .Select(e => e.Peer)
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray());
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

            foreach (var gone in sent.Keys.Except(lists.Keys).ToArray())
            {
                sent.Remove(gone);
            }

            foreach (var (connection, list) in lists)
            {
                var key = string.Join('|', list.Peers.Select(p => $"{p.NodeId}@{p.Endpoint}#{p.Thumbprint}"));

                if (sent.TryGetValue(connection, out var last) ? last == key : list.Peers.Count == 0)
                {
                    continue;
                }

                try
                {
                    await hub.Clients.Client(connection).SendAsync("ShardPeers", list, cancellationToken);
                    sent[connection] = key;
                    logger.LogInformation("Shard peers for connection {Connection}: {Peers}", connection, list.Peers.Count == 0 ? "none" : string.Join(", ", list.Peers.Select(p => $"{p.Name} ({p.Endpoint})")));
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

            var (groupLists, status) = ComputeGroups(lists);

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
