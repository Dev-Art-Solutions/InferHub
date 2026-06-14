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
            _ => new NodeRegistryEntry(normalized, now, 0, Array.Empty<ModelInfo>(), null),
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

    public bool ReportModels(string connectionId, NodeModels models, DateTimeOffset now)
    {
        if (!nodes.TryGetValue(connectionId, out var existing))
        {
            return false;
        }

        var normalizedModels = (models.Models ?? Array.Empty<ModelInfo>())
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(model => model with { Name = model.Name.Trim() })
            .ToArray();

        nodes[connectionId] = existing with
        {
            LastSeenUtc = now,
            Models = normalizedModels,
            ModelsRefreshedAt = models.RefreshedAt
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

    public IReadOnlyCollection<ModelInfo> DistinctModels()
    {
        return nodes
            .Values
            .SelectMany(entry => entry.Models.Select(model => new { entry.Registration, model }))
            .OrderBy(item => item.model.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Registration.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Registration.NodeId, StringComparer.OrdinalIgnoreCase)
            .GroupBy(item => item.model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().model)
            .ToArray();
    }

    public IReadOnlyCollection<RoutableNode> FindNodesWithModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Array.Empty<RoutableNode>();
        }

        return nodes
            .Where(pair => pair.Value.Models.Any(candidate => ModelNamesMatch(candidate.Name, model)))
            .Select(pair => new RoutableNode(
                pair.Key,
                pair.Value.Registration.NodeId,
                pair.Value.Registration.Name))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.ConnectionId, StringComparer.OrdinalIgnoreCase)
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
            entry.InFlight,
            entry.Models.Count);
    }

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool ModelNamesMatch(string candidate, string requested)
    {
        return string.Equals(candidate.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record NodeRegistryEntry(
        NodeRegistration Registration,
        DateTimeOffset LastSeenUtc,
        int InFlight,
        IReadOnlyList<ModelInfo> Models,
        DateTimeOffset? ModelsRefreshedAt);
}
