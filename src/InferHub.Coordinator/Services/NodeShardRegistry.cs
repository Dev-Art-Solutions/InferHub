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
    InferHub.Coordinator.Auth.NodeCertificateAuthority? authority = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, string> sent = new(StringComparer.Ordinal);

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
