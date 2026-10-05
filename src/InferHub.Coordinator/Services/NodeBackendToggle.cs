using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Starts or stops one engine on one node (phase 95) by writing <c>backends[name]</c> into the
/// profile that matches it — <see cref="NodeModelToggle"/>'s read-modify-write, one field over.
/// </summary>
/// <remarks>
/// <para>
/// <b>A profile, not a command</b> (43 D2): an engine stopped from the console stays stopped across a
/// reboot of the node and a restart of the hub (with profile persistence on), because the node asks
/// for its profile at registration and converges. A one-shot command would be undone by the first
/// reconnect.
/// </para>
/// <para>
/// The hub refuses a name the node never reported, for a better message. <b>The node's clamp is the
/// copy that is load-bearing</b> (43 D1): <c>Backend:Engines</c> on the box is the grant, and a
/// profile written by any other route that names an engine the box lacks is refused there anyway.
/// </para>
/// </remarks>
public sealed class NodeBackendToggle(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    NodeBackendRegistry backends,
    IAuditLog audit,
    ILogger<NodeBackendToggle> logger)
{
    public async Task<BackendToggleOutcome> SetRunningAsync(
        NodeSnapshot node,
        string backend,
        bool running,
        string by,
        CancellationToken cancellationToken)
    {
        backend = (backend ?? string.Empty).Trim();

        if (backends.Of(node.NodeId) is not { } state)
        {
            return BackendToggleOutcome.Refused(
                BackendToggleOutcome.NoEngines,
                $"node '{node.NodeId}' reports no engines: it runs a single Backend:Type (or a release before v3.60). List its engines under Backend:Engines on the box to start and stop them from here.");
        }

        var engine = state.Engines.FirstOrDefault(e => string.Equals(e.Name, backend, StringComparison.OrdinalIgnoreCase));

        if (engine is null)
        {
            return BackendToggleOutcome.Refused(
                BackendToggleOutcome.UnknownEngine,
                $"node '{node.NodeId}' has no engine '{backend}'; it has {string.Join(", ", state.Engines.Select(e => e.Name))}. Engines are added in the node's own Backend:Engines, never from the hub.");
        }

        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return BackendToggleOutcome.Refused(
                BackendToggleOutcome.ProfileConflict,
                $"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles ({string.Join(", ", assignment.Conflicts)}); resolve the conflict in the raw profile editor before starting or stopping an engine here");
        }

        var name = assignment.Profile?.Name ?? $"node:{node.NodeId}";
        var existing = assignment.Profile ?? new NodeProfile(name, 0, new NodeProfileSelector(NodeId: node.NodeId));

        var wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in existing.Backends ?? new Dictionary<string, bool>())
        {
            wanted[pair.Key] = pair.Value;
        }

        wanted[engine.Name] = running;

        var stored = profiles.Put(name, existing with { Backends = wanted });
        await coordinator.ReassertAsync(cancellationToken);

        audit.Record(node.NodeId, $"backend.{(running ? "start" : "stop")}:{engine.Name}", by, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Engine '{Engine}' {State} on node {NodeId} via profile '{Profile}' revision {Revision} (by {By})",
            engine.Name, running ? "started" : "stopped", node.NodeId, stored.Name, stored.Revision, by);

        return new BackendToggleOutcome(null, null, engine.Name, stored);
    }
}

public sealed record BackendToggleOutcome(string? Refusal, string? Error, string? Engine, NodeProfile? Profile)
{
    /// <summary>The node runs one backend, or is older than v3.60. A 409: the fix is on the box.</summary>
    public const string NoEngines = "no-engines";

    /// <summary>The node never reported that name. A 404 naming the ones it has.</summary>
    public const string UnknownEngine = "unknown-engine";

    /// <summary>Two profiles match the node (43 D4). A 409, as for a model toggle.</summary>
    public const string ProfileConflict = "conflict";

    public bool Applied => Refusal is null;

    public static BackendToggleOutcome Refused(string refusal, string error) => new(refusal, error, null, null);
}
