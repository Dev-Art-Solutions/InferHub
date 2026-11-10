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
    ShardGroupState? Reported);

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
            var members = definition.Members.Select(m => (Key: m, Node: Find(m))).Where(m => !string.Equals(m.Node?.NodeId, head?.NodeId, StringComparison.OrdinalIgnoreCase) || m.Node is null).ToArray();
            var reported = head is null ? null : shards.Of(head.NodeId)?.Groups?.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            string? why = null;

            if (string.IsNullOrWhiteSpace(definition.Model))
            {
                why = "no Model: the file on the head to run";
            }
            else if (head is null)
            {
                why = $"the head '{definition.Head}' is not connected";
            }
            else if (members.Length == 0)
            {
                why = "no members besides the head: a group of one is the head's own llama.cpp";
            }
            else if (members.FirstOrDefault(m => m.Node is null) is { Key: { } absent } && members.Any(m => m.Node is null))
            {
                why = $"member '{absent}' is not connected";
            }
            else
            {
                var everyone = new[] { head }.Concat(members.Select(m => m.Node!)).ToArray();
                var states = everyone.Select(n => (Node: n, State: shards.Of(n.NodeId))).ToArray();
                var listed = peerLists.TryGetValue(head.ConnectionId, out var headList) ? headList.Peers.Select(p => p.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];

                if (states.FirstOrDefault(s => s.State is not { State: NodeShardState.Running }) is { Node: { } idle })
                {
                    why = $"{idle.Name}'s worker is not running (Node:Shards)";
                }
                else if (states.FirstOrDefault(s => s.State!.Tunnel is not { Off: null }) is { Node: { } closed })
                {
                    why = $"{closed.Name}'s tunnel is not open: {shards.Of(closed.NodeId)?.Tunnel?.Off ?? "no tunnel"}";
                }
                else if (members.FirstOrDefault(m => !listed.Contains(m.Node!.NodeId)) is { Node: { } unlisted })
                {
                    why = $"{unlisted.Name} is not one of {head.Name}'s peers (a certificate, the same organisation — 135)";
                }
                else if (states.Select(s => s.State!.Protocol).Distinct().Count() > 1)
                {
                    why = "the members' llama.cpp RPC protocols differ: " + string.Join(", ", states.Select(s => $"{s.Node.Name} v{s.State!.Protocol}"));
                }
                else
                {
                    if (!lists.TryGetValue(head.ConnectionId, out var specs))
                    {
                        lists[head.ConnectionId] = specs = [];
                    }

                    specs.Add(new ShardGroupSpec(
                        name,
                        definition.Model!.Trim(),
                        definition.Context,
                        Weight(states[0].State!),
                        members.Select(m => new ShardGroupWorker(m.Node!.NodeId, m.Node.Name, Weight(shards.Of(m.Node.NodeId)!))).ToArray()));
                }
            }

            status.Add(new ShardGroupStatus(name, head?.Name ?? definition.Head, definition.Members, definition.Model, why is null, why, reported));
        }

        var result = nodes.ToDictionary(n => n.ConnectionId, n => new ShardGroupList(lists.TryGetValue(n.ConnectionId, out var s) ? s : []), StringComparer.Ordinal);
        return (result, status);
    }

    private static long Weight(NodeShardState state) => Math.Max(1, state.RamMiB + state.VramMiB);

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
