using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about its colibri catalogue (phase 97) — 95's registry, one report
/// over. In memory (rule 4); the hub never asks (44 D6).
/// </summary>
public sealed class NodeColibriRegistry
{
    private readonly ConcurrentDictionary<string, NodeColibriState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeColibriState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeColibriState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());
}

/// <summary>
/// Pins or unpins one catalogue model, or switches on-demand, on one node (phase 97) by writing
/// <c>colibri</c> into the profile that matches it — <see cref="NodeBackendToggle"/>'s read-modify-write.
/// </summary>
/// <remarks>
/// <b>A profile, not a command</b> (43 D2): a model pinned from the console is loaded again after a
/// reboot of either side. The hub refuses a model the node never reported for a better message; <b>the
/// node's clamp is the copy that is load-bearing</b> (43 D1) — its catalogue and its
/// <c>Serve:MaxLoaded</c> are the ceiling.
/// </remarks>
public sealed class NodeColibriToggle(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    NodeColibriRegistry colibri,
    IAuditLog audit,
    ILogger<NodeColibriToggle> logger)
{
    public Task<ColibriToggleOutcome> SetLoadedAsync(NodeSnapshot node, string model, bool loaded, string by, CancellationToken cancellationToken)
    {
        model = (model ?? string.Empty).Trim();

        if (colibri.Of(node.NodeId) is not { } state)
        {
            return Task.FromResult(NoCatalogue(node));
        }

        var known = state.Models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            return Task.FromResult(ColibriToggleOutcome.Refused(
                ColibriToggleOutcome.UnknownModel,
                $"node '{node.NodeId}' has no colibri model '{model}'; it has {string.Join(", ", state.Models.Select(m => m.Name))}. Models are converted into the node's Colibri:Serve:ModelsDir, never added from the hub."));
        }

        return WriteAsync(node, by, $"colibri.{(loaded ? "load" : "unload")}:{known.Name}", block =>
        {
            var pins = (block?.Loaded ?? state.Models.Where(m => m.Pinned).Select(m => m.Name).ToArray())
                .Where(name => !string.Equals(name, known.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (loaded)
            {
                // Selecting a model on a full node is a switch, not a refusal: the oldest pin gives
                // way, which on the default MaxLoaded=1 is "this one instead of that one".
                while (pins.Count > 0 && pins.Count >= state.MaxLoaded)
                {
                    pins.RemoveAt(0);
                }

                pins.Add(known.Name);
            }

            return (block ?? new ColibriProfile()) with { Loaded = pins };
        }, cancellationToken);
    }

    public Task<ColibriToggleOutcome> SetOnDemandAsync(NodeSnapshot node, bool onDemand, string by, CancellationToken cancellationToken)
    {
        if (colibri.Of(node.NodeId) is not { } state)
        {
            return Task.FromResult(NoCatalogue(node));
        }

        return WriteAsync(
            node,
            by,
            $"colibri.on-demand:{(onDemand ? "on" : "off")}",
            block => (block ?? new ColibriProfile()) with { OnDemand = onDemand },
            cancellationToken);
    }

    private async Task<ColibriToggleOutcome> WriteAsync(
        NodeSnapshot node,
        string by,
        string action,
        Func<ColibriProfile?, ColibriProfile> change,
        CancellationToken cancellationToken)
    {
        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return ColibriToggleOutcome.Refused(
                ColibriToggleOutcome.ProfileConflict,
                $"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles ({string.Join(", ", assignment.Conflicts)}); resolve the conflict in the raw profile editor first");
        }

        var name = assignment.Profile?.Name ?? $"node:{node.NodeId}";
        var existing = assignment.Profile ?? new NodeProfile(name, 0, new NodeProfileSelector(NodeId: node.NodeId));

        var block = change(existing.Colibri);
        var stored = profiles.Put(name, existing with { Colibri = block });
        await coordinator.ReassertAsync(cancellationToken);

        audit.Record(node.NodeId, action, by, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "{Action} on node {NodeId} via profile '{Profile}' revision {Revision} (by {By})",
            action, node.NodeId, stored.Name, stored.Revision, by);

        return new ColibriToggleOutcome(null, null, stored);
    }

    private static ColibriToggleOutcome NoCatalogue(NodeSnapshot node) => ColibriToggleOutcome.Refused(
        ColibriToggleOutcome.NoCatalogue,
        $"node '{node.NodeId}' reports no colibri catalogue: it runs one Colibri:Serve:Model, no colibri at all, or a release before v3.62. Point Colibri:Serve:ModelsDir at its converted models to pick them from here.");
}

public sealed record ColibriToggleOutcome(string? Refusal, string? Error, NodeProfile? Profile)
{
    /// <summary>A 409: the fix is on the box.</summary>
    public const string NoCatalogue = "no-catalogue";

    /// <summary>A 404 naming the models it has.</summary>
    public const string UnknownModel = "unknown-model";

    public const string ProfileConflict = "conflict";

    public bool Applied => Refusal is null;

    public static ColibriToggleOutcome Refused(string refusal, string error) => new(refusal, error, null);
}
