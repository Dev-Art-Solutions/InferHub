using System.Collections.Concurrent;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The last thing each node said about one engine's catalogue — colibri's (phase 97) or Strata's (99) —
/// 95's registry, one report over. In memory (rule 4); the hub never asks (44 D6).
/// </summary>
public abstract class NodeCatalogRegistry
{
    private readonly ConcurrentDictionary<string, NodeCatalogState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeCatalogState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeCatalogState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());
}

/// <summary>Phase 97: what each node's colibri catalogue holds.</summary>
public sealed class NodeColibriRegistry : NodeCatalogRegistry;

/// <summary>Phase 99: what each node's Strata catalogue holds, and what it could install.</summary>
public sealed class NodeStrataRegistry : NodeCatalogRegistry;

/// <summary>
/// Pins or unpins one catalogue model, or switches on-demand, on one node by writing the engine's block
/// into the profile that matches it — <see cref="NodeBackendToggle"/>'s read-modify-write. Phase 97 for
/// colibri; phase 99 made the engine a parameter, because Strata's catalogue is the same shape.
/// </summary>
/// <remarks>
/// <b>A profile, not a command</b> (43 D2): a model pinned from the console is loaded again after a
/// reboot of either side. The hub refuses a model the node never reported for a better message; <b>the
/// node's clamp is the copy that is load-bearing</b> (43 D1) — its catalogue and its
/// <c>Serve:MaxLoaded</c> are the ceiling.
/// </remarks>
public abstract class NodeCatalogToggle(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    NodeCatalogRegistry catalogues,
    IAuditLog audit,
    ILogger logger)
{
    /// <summary>The engine, as the profile key and the audit action spell it.</summary>
    public abstract string Engine { get; }

    /// <summary>How a model gets into this engine's catalogue — the end of the "no such model" sentence.</summary>
    protected abstract string HowModelsArrive { get; }

    protected abstract string NoCatalogueSentence(string nodeId);

    protected abstract CatalogProfile? BlockOf(NodeProfile profile);

    protected abstract NodeProfile WithBlock(NodeProfile profile, CatalogProfile block);

    public Task<CatalogToggleOutcome> SetLoadedAsync(NodeSnapshot node, string model, bool loaded, string by, CancellationToken cancellationToken)
    {
        model = (model ?? string.Empty).Trim();

        if (catalogues.Of(node.NodeId) is not { } state)
        {
            return Task.FromResult(NoCatalogue(node));
        }

        var known = state.Models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));

        if (known is null)
        {
            return Task.FromResult(CatalogToggleOutcome.Refused(
                CatalogToggleOutcome.UnknownModel,
                $"node '{node.NodeId}' has no {Engine} model '{model}'; it has {string.Join(", ", state.Models.Select(m => m.Name))}. {HowModelsArrive}"));
        }

        return WriteAsync(node, by, $"{Engine}.{(loaded ? "load" : "unload")}:{known.Name}", block =>
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

            return (block ?? new CatalogProfile()) with { Loaded = pins };
        }, cancellationToken);
    }

    public Task<CatalogToggleOutcome> SetOnDemandAsync(NodeSnapshot node, bool onDemand, string by, CancellationToken cancellationToken)
    {
        if (catalogues.Of(node.NodeId) is null)
        {
            return Task.FromResult(NoCatalogue(node));
        }

        return WriteAsync(
            node,
            by,
            $"{Engine}.on-demand:{(onDemand ? "on" : "off")}",
            block => (block ?? new CatalogProfile()) with { OnDemand = onDemand },
            cancellationToken);
    }

    private async Task<CatalogToggleOutcome> WriteAsync(
        NodeSnapshot node,
        string by,
        string action,
        Func<CatalogProfile?, CatalogProfile> change,
        CancellationToken cancellationToken)
    {
        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return CatalogToggleOutcome.Refused(
                CatalogToggleOutcome.ProfileConflict,
                $"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles ({string.Join(", ", assignment.Conflicts)}); resolve the conflict in the raw profile editor first");
        }

        var name = assignment.Profile?.Name ?? $"node:{node.NodeId}";
        var existing = assignment.Profile ?? new NodeProfile(name, 0, new NodeProfileSelector(NodeId: node.NodeId));

        var block = change(BlockOf(existing));
        var stored = profiles.Put(name, WithBlock(existing, block));
        await coordinator.ReassertAsync(cancellationToken);

        audit.Record(node.NodeId, action, by, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "{Action} on node {NodeId} via profile '{Profile}' revision {Revision} (by {By})",
            action, node.NodeId, stored.Name, stored.Revision, by);

        return new CatalogToggleOutcome(null, null, stored);
    }

    private CatalogToggleOutcome NoCatalogue(NodeSnapshot node) => CatalogToggleOutcome.Refused(
        CatalogToggleOutcome.NoCatalogue,
        NoCatalogueSentence(node.NodeId));
}

/// <summary>Phase 97: the <c>colibri</c> block.</summary>
public sealed class NodeColibriToggle(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    NodeColibriRegistry colibri,
    IAuditLog audit,
    ILogger<NodeColibriToggle> logger) : NodeCatalogToggle(profiles, coordinator, colibri, audit, logger)
{
    public override string Engine => "colibri";

    protected override string HowModelsArrive =>
        "Models are converted into the node's Colibri:Serve:ModelsDir, never added from the hub.";

    protected override string NoCatalogueSentence(string nodeId) =>
        $"node '{nodeId}' reports no colibri catalogue: it runs one Colibri:Serve:Model, no colibri at all, or a release before v3.62. Point Colibri:Serve:ModelsDir at its converted models to pick them from here.";

    protected override CatalogProfile? BlockOf(NodeProfile profile) => profile.Colibri;

    protected override NodeProfile WithBlock(NodeProfile profile, CatalogProfile block) => profile with { Colibri = block };
}

/// <summary>Phase 99: the <c>strata</c> block.</summary>
public sealed class NodeStrataToggle(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    NodeStrataRegistry strata,
    IAuditLog audit,
    ILogger<NodeStrataToggle> logger) : NodeCatalogToggle(profiles, coordinator, strata, audit, logger)
{
    public override string Engine => "strata";

    protected override string HowModelsArrive =>
        "Models are installed by Strata's setup on the node — on the box, or from this hub's Strata panel (a Hugging Face link).";

    protected override string NoCatalogueSentence(string nodeId) =>
        $"node '{nodeId}' reports no Strata catalogue: it runs no strata backend or engine with Strata:Root, or a release before v3.64. Point Strata:Root at a Strata install to pick its models from here.";

    protected override CatalogProfile? BlockOf(NodeProfile profile) => profile.Strata;

    protected override NodeProfile WithBlock(NodeProfile profile, CatalogProfile block) => profile with { Strata = block };
}

public sealed record CatalogToggleOutcome(string? Refusal, string? Error, NodeProfile? Profile)
{
    /// <summary>A 409: the fix is on the box.</summary>
    public const string NoCatalogue = "no-catalogue";

    /// <summary>A 404 naming the models it has.</summary>
    public const string UnknownModel = "unknown-model";

    public const string ProfileConflict = "conflict";

    public bool Applied => Refusal is null;

    public static CatalogToggleOutcome Refused(string refusal, string error) => new(refusal, error, null);
}
