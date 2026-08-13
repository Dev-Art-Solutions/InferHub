using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

public sealed class NodeRegistry : INodeRegistry
{
    private readonly ConcurrentDictionary<string, NodeRegistryEntry> nodes = new();
    private readonly ConcurrentDictionary<string, StrongBox<int>> localInFlight = new();

    public event Action? Changed;

    private void RaiseChanged() => Changed?.Invoke();

    public void Upsert(string connectionId, NodeRegistration registration, DateTimeOffset now)
    {
        var normalized = registration with
        {
            NodeId = Normalize(registration.NodeId, "unknown"),
            Name = Normalize(registration.Name, "unnamed-node"),
            OllamaEndpoint = Normalize(registration.OllamaEndpoint, "unknown"),
            Version = Normalize(registration.Version, "unknown"),
            Labels = NormalizeLabels(registration.Labels),
            MaxConcurrency = NormalizeMaxConcurrency(registration.MaxConcurrency)
        };

        nodes.AddOrUpdate(
            connectionId,
            _ => Resolved(new NodeRegistryEntry(
                normalized,
                now,
                0,
                Array.Empty<ModelInfo>(),
                null,
                false,
                normalized.Capabilities,
                Array.Empty<NodeCapability>(),
                StreamedAttachments: normalized.SupportsStreamedAttachments)),
            (_, existing) => Resolved(existing with
            {
                Registration = normalized,
                LastSeenUtc = now,
                // A message that does not carry a declaration does not erase one. The node
                // declares on the model report (see NodeRegistration.Capabilities), and a
                // reconnect re-registers before it re-reports.
                DeclaredCapabilities = normalized.Capabilities ?? existing.DeclaredCapabilities,
                StreamedAttachments = normalized.SupportsStreamedAttachments ?? existing.StreamedAttachments
            }));

        localInFlight.GetOrAdd(connectionId, _ => new StrongBox<int>(0));
        RaiseChanged();
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

        nodes[connectionId] = Resolved(existing with
        {
            LastSeenUtc = now,
            Models = normalizedModels,
            ModelsRefreshedAt = models.RefreshedAt,
            DeclaredCapabilities = models.Capabilities ?? existing.DeclaredCapabilities,
            StreamedAttachments = models.SupportsStreamedAttachments ?? existing.StreamedAttachments
        });

        RaiseChanged();
        return true;
    }

    public bool Remove(string connectionId)
    {
        localInFlight.TryRemove(connectionId, out _);
        if (!nodes.TryRemove(connectionId, out _))
        {
            return false;
        }

        RaiseChanged();
        return true;
    }

    public bool SetEffectiveConcurrency(string connectionId, int? maxConcurrency)
    {
        if (!nodes.TryGetValue(connectionId, out var existing))
        {
            return false;
        }

        var effective = NormalizeMaxConcurrency(maxConcurrency);

        if (existing.EffectiveMaxConcurrency == effective)
        {
            return true;
        }

        nodes[connectionId] = existing with { EffectiveMaxConcurrency = effective };
        RaiseChanged();
        return true;
    }

    public bool Cordon(string nodeId)
    {
        return SetCordoned(nodeId, true);
    }

    public bool Uncordon(string nodeId)
    {
        return SetCordoned(nodeId, false);
    }

    public string? FindConnectionIdByNodeId(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return null;
        }

        var trimmed = nodeId.Trim();

        foreach (var pair in nodes)
        {
            if (string.Equals(pair.Value.Registration.NodeId, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    private bool SetCordoned(string nodeId, bool cordoned)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        var trimmed = nodeId.Trim();
        var matched = false;
        var transitioned = false;

        foreach (var pair in nodes)
        {
            if (!string.Equals(pair.Value.Registration.NodeId, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var existing = pair.Value;

            if (existing.Cordoned == cordoned)
            {
                matched = true;
                continue;
            }

            if (nodes.TryUpdate(pair.Key, existing with { Cordoned = cordoned }, existing))
            {
                matched = true;
                transitioned = true;
            }
        }

        if (transitioned)
        {
            RaiseChanged();
        }

        return matched;
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

    public IReadOnlyCollection<NodeModelInventory> ModelInventory()
    {
        return nodes
            .Select(pair => new NodeModelInventory(
                pair.Key,
                pair.Value.Registration.NodeId,
                pair.Value.Registration.Name,
                pair.Value.Cordoned,
                pair.Value.Registration.SupportsModelManagement,
                pair.Value.Models))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<CapabilitySummary> CapabilitySummary()
    {
        return nodes
            .Values
            .Where(entry => !entry.Cordoned)
            .SelectMany(entry => entry.Capabilities)
            .GroupBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CapabilitySummary(
                group.Key,
                group.Count(),
                group
                    .SelectMany(capability => capability.Models)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(summary => summary.Capability, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<RoutableNode> FindNodesWithModel(
        string model,
        string? capability = null,
        bool requireStreamedAttachments = false)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Array.Empty<RoutableNode>();
        }

        return nodes
            .Where(pair => !pair.Value.Cordoned
                && (!requireStreamedAttachments || pair.Value.StreamedAttachments is true)
                && (capability is null
                    ? pair.Value.Models.Any(candidate => ModelNamesMatch(candidate.Name, model))
                    : NodeCapabilityResolver.Provides(pair.Value.Capabilities, capability, model)))
            .Select(pair => new RoutableNode(
                pair.Key,
                pair.Value.Registration.NodeId,
                pair.Value.Registration.Name))
            .OrderBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.NodeId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.ConnectionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int IncrementInFlight(string connectionId)
    {
        var box = localInFlight.GetOrAdd(connectionId, _ => new StrongBox<int>(0));
        return Interlocked.Increment(ref box.Value);
    }

    public int DecrementInFlight(string connectionId)
    {
        if (!localInFlight.TryGetValue(connectionId, out var box))
        {
            return 0;
        }

        var value = Interlocked.Decrement(ref box.Value);

        if (value < 0)
        {
            Interlocked.Exchange(ref box.Value, 0);
            return 0;
        }

        return value;
    }

    public int GetLocalInFlight(string connectionId)
    {
        if (!localInFlight.TryGetValue(connectionId, out var box))
        {
            return 0;
        }

        return Math.Max(0, Volatile.Read(ref box.Value));
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
                localInFlight.TryRemove(pair.Key, out _);
                evicted.Add(ToSnapshot(pair.Key, removed, now));
            }
        }

        if (evicted.Count > 0)
        {
            RaiseChanged();
        }

        return evicted;
    }

    private NodeSnapshot ToSnapshot(string connectionId, NodeRegistryEntry entry, DateTimeOffset now)
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
            GetLocalInFlight(connectionId),
            entry.Models.Count,
            entry.Registration.Labels ?? EmptyLabels,
            // A profile can only ever have lowered it (phase-43 D1), so this is the number the
            // saturation check and the console should both be reading.
            entry.EffectiveMaxConcurrency ?? entry.Registration.MaxConcurrency,
            entry.Cordoned,
            entry.Registration.SupportsModelManagement,
            entry.Capabilities);
    }

    /// <summary>
    /// Recomputes the resolved capability set. Called on every write that can change either half
    /// of the input — the declaration or the model list — so the resolved set is never stale and
    /// nothing downstream resolves it a second time (phase-40 D1).
    /// </summary>
    private static NodeRegistryEntry Resolved(NodeRegistryEntry entry) => entry with
    {
        Capabilities = NodeCapabilityResolver.Resolve(entry.DeclaredCapabilities, entry.Models)
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static IReadOnlyDictionary<string, string> NormalizeLabels(
        IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return EmptyLabels;
        }

        var copy = new Dictionary<string, string>(labels.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in labels)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            copy[pair.Key.Trim()] = pair.Value ?? string.Empty;
        }

        return copy;
    }

    private static int? NormalizeMaxConcurrency(int? maxConcurrency)
    {
        return maxConcurrency is { } cap && cap >= 1 ? cap : null;
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
        DateTimeOffset? ModelsRefreshedAt,
        bool Cordoned,
        /// What the node said, verbatim — null when it said nothing (phase 40).
        IReadOnlyList<NodeCapability>? DeclaredCapabilities,
        /// What the router asks. Derived from the two fields above by <see cref="Resolved"/>.
        IReadOnlyList<NodeCapability> Capabilities,
        /// The cap after a profile clamped it (phase 43). Null = the registered value stands.
        int? EffectiveMaxConcurrency = null,
        /// Whether the node can pull a streamed attachment (phase 53). Null is what every node
        /// before v3.21 says, and it is read as "no" — the same null-is-not-a-declaration shape as
        /// <see cref="DeclaredCapabilities"/>, so a message carrying nothing erases nothing.
        bool? StreamedAttachments = null);
}
