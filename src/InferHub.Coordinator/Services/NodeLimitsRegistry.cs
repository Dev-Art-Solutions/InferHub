using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about its runtime limits (phase 102) — phase 45's registry once more.
/// In memory (rule 4): a coordinator restart forgets it and every node re-reports at registration.
/// </summary>
public sealed class NodeLimitsRegistry
{
    private readonly ConcurrentDictionary<string, NodeLimitsState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeLimitsState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeLimitsState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}

/// <summary>
/// Sets or clears one runtime's limits on one node (phase 102) by writing <c>limits[group]</c> into the
/// profile that matches it — <see cref="NodeBackendToggle"/>'s read-modify-write, one field over.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node's ceiling is shown here and never written.</b> A value above what the node reported is
/// refused with both numbers, for the message; <b>the node's clamp is the copy that is load-bearing</b>
/// (43 D1), so a profile written by any other route that asks for more is refused there anyway and the
/// node's own value stays in force.
/// </para>
/// <para>
/// A node that has not reported (older than v3.67, or not connected yet) is not refused: limiting is
/// narrowing, and the node clamps whatever arrives.
/// </para>
/// </remarks>
public sealed class NodeLimitsControl(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    NodeLimitsRegistry limits,
    IAuditLog audit,
    ILogger<NodeLimitsControl> logger)
{
    public const string UnknownGroup = "unknown-group";
    public const string AboveCeiling = "above-ceiling";
    public const string Invalid = "invalid";
    public const string ProfileConflict = "conflict";

    /// <summary>Null <paramref name="wanted"/> (or an empty one) clears the coordinator's limits on that runtime.</summary>
    public async Task<(string? Refusal, string? Error, NodeProfile? Profile)> SetAsync(
        NodeSnapshot node,
        string group,
        EngineLimits? wanted,
        string by,
        CancellationToken cancellationToken)
    {
        if (EngineLimitGroups.Normalize(group) is not { } name)
        {
            return (UnknownGroup, $"'{group}' names no runtime; a limit names one of {string.Join(", ", EngineLimitGroups.All)}", null);
        }

        if (wanted is not null && Check(node.NodeId, name, wanted) is { } problem)
        {
            return problem;
        }

        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return (ProfileConflict, $"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles ({string.Join(", ", assignment.Conflicts)}); resolve the conflict in the raw profile editor before setting limits here", null);
        }

        var profileName = assignment.Profile?.Name ?? $"node:{node.NodeId}";
        var existing = assignment.Profile ?? new NodeProfile(profileName, 0, new NodeProfileSelector(NodeId: node.NodeId));

        var map = new Dictionary<string, EngineLimits>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in existing.Limits ?? new Dictionary<string, EngineLimits>())
        {
            if (EngineLimitGroups.Normalize(pair.Key) is { } key && pair.Value is not null)
            {
                map[key] = pair.Value;
            }
        }

        if (wanted is null || wanted.IsEmpty)
        {
            map.Remove(name);
        }
        else
        {
            map[name] = wanted;
        }

        var stored = profiles.Put(profileName, existing with { Limits = map.Count == 0 ? null : map });
        await coordinator.ReassertAsync(cancellationToken);

        var action = wanted is null || wanted.IsEmpty ? $"limits.clear:{name}" : $"limits.set:{name}";
        audit.Record(node.NodeId, action, by, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "{Action} on node {NodeId} via profile '{Profile}' revision {Revision} (by {By})",
            action, node.NodeId, stored.Name, stored.Revision, by);

        return (null, null, stored);
    }

    /// <summary>The hub's copy of the node's clamp, for a 409 that names the node's number instead of a silent refusal later.</summary>
    private (string?, string?, NodeProfile?)? Check(string nodeId, string group, EngineLimits wanted)
    {
        if (wanted.CpuPercent is < 1 or > 100)
        {
            return (Invalid, $"cpuPercent must be 1–100 (got {wanted.CpuPercent})", null);
        }

        foreach (var (field, value) in new[] { ("cores", wanted.Cores), ("memoryMiB", wanted.MemoryMiB), ("vramMiB", wanted.VramMiB) })
        {
            if (value is < 1)
            {
                return (Invalid, $"{field} must be at least 1 (got {value})", null);
            }
        }

        if (wanted.Gpus is { } gpus && gpus.Any(index => index < 0))
        {
            return (Invalid, "a GPU index cannot be negative", null);
        }

        var ceiling = limits.Of(nodeId)?.Groups
            .FirstOrDefault(g => string.Equals(g.Group, group, StringComparison.OrdinalIgnoreCase))?.Node;

        if (ceiling is null)
        {
            return null;
        }

        foreach (var (field, cap, value, unit) in new[]
                 {
                     ("cpuPercent", ceiling.CpuPercent, wanted.CpuPercent, "%"),
                     ("cores", ceiling.Cores, wanted.Cores, " core(s)"),
                     ("memoryMiB", ceiling.MemoryMiB, wanted.MemoryMiB, " MiB"),
                     ("vramMiB", ceiling.VramMiB, wanted.VramMiB, " MiB")
                 })
        {
            if (cap is { } c && value is { } v && v > c)
            {
                return (AboveCeiling, $"node '{nodeId}' limits {group} {field} to {c}{unit} itself (Node:ResourceLimits:Engines:{group}); a coordinator can set it lower, never {v}{unit}", null);
            }
        }

        if (ceiling.Gpus is { } allowed && wanted.Gpus is { } asked && asked.Except(allowed).ToArray() is { Length: > 0 } outside)
        {
            return (AboveCeiling, $"node '{nodeId}' lets {group} see GPU(s) [{string.Join(", ", allowed)}] only; a coordinator can pick from them, never add {string.Join(", ", outside)}", null);
        }

        return null;
    }
}
