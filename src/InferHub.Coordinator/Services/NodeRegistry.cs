using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

public sealed class NodeRegistry : INodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeRegistryEntry> nodes = new();

    public void Upsert(string connectionId, NodeRegistration registration, DateTimeOffset now)
    {
        var normalized = registration with
        {
            NodeId = Normalize(registration.NodeId, "unknown"),
            Name = Normalize(registration.Name, "unnamed-node"),
            OllamaEndpoint = Normalize(registration.OllamaEndpoint, "unknown"),
            Version = Normalize(registration.Version, "unknown")
        };

        nodes.AddOrUpdate(
            connectionId,
            _ => new NodeRegistryEntry(normalized, now, 0),
            (_, existing) => existing with
            {
                Registration = normalized,
                LastSeenUtc = now
            });
    }

    public bool Touch(string connectionId, Heartbeat heartbeat, DateTimeOffset now)
    {
        if (!nodes.TryGetValue(connectionId, out var existing))
        {
            return false;
        }

        nodes[connectionId] = existing with
        {
            LastSeenUtc = now,
            InFlight = Math.Max(0, heartbeat.InFlight)
        };

        return true;
    }

    public bool Remove(string connectionId)
    {
        return nodes.TryRemove(connectionId, out _);
    }

    public IReadOnlyCollection<NodeSnapshot> Snapshot(DateTimeOffset now)
    {
        return nodes
            .Select(pair => ToSnapshot(pair.Key, pair.Value, now))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<NodeSnapshot> EvictStale(DateTimeOffset cutoffUtc, DateTimeOffset now)
    {
        var evicted = new List<NodeSnapshot>();

        foreach (var pair in nodes)
        {
            if (pair.Value.LastSeenUtc >= cutoffUtc)
            {
                continue;
            }

            if (nodes.TryRemove(pair.Key, out var removed))
            {
                evicted.Add(ToSnapshot(pair.Key, removed, now));
            }
        }

        return evicted;
    }

    private static NodeSnapshot ToSnapshot(string connectionId, NodeRegistryEntry entry, DateTimeOffset now)
    {
        var ageSeconds = Math.Max(0, (now - entry.LastSeenUtc).TotalSeconds);

        return new NodeSnapshot(
            connectionId,
            entry.Registration.NodeId,
            entry.Registration.Name,
            entry.Registration.OllamaEndpoint,
            entry.Registration.Version,
            entry.LastSeenUtc,
            ageSeconds,
            entry.InFlight);
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record NodeRegistryEntry(
        NodeRegistration Registration,
        DateTimeOffset LastSeenUtc,
        int InFlight);
}
