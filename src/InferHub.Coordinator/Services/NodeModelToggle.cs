using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Enables or disables one model on one node (phase 74's profile read-modify-write), extracted from
/// <c>AdminEndpoints</c> in phase 76 so the auto-scaler can call the exact same path a human admin
/// does — same VRAM precheck, same profile mechanism, same audit trail — rather than a second way to
/// flip a model on. <b>One place</b>, the way <c>CollectionOwnership</c> and <c>CollectionAccessPolicy</c>
/// are one place.
/// </summary>
public sealed class NodeModelToggle(
    IProfileRegistry profiles,
    NodeProfileCoordinator coordinator,
    INodeRegistry registry,
    IAuditLog audit,
    ILogger<NodeModelToggle> logger)
{
    /// <summary>
    /// Estimates whether a node has room for a model it does not necessarily hold yet, and gates on
    /// it. There is no measured VRAM/RAM footprint anywhere in this codebase for an Ollama-style
    /// model (unlike an image recipe, which declares one) — <see cref="ModelInfo.SizeBytes"/> is
    /// on-disk weight size, not resident size, so this is deliberately labeled an estimate rather
    /// than treated as a fact the way image-recipe VRAM gating is.
    /// </summary>
    public ModelPrecheckResult Precheck(NodeSnapshot node, string model)
    {
        var budgetMiB = node.VramBudgetMiB ?? 0;
        var reserveMiB = node.VramReserveMiB ?? 0;

        if (budgetMiB <= 0)
        {
            // Same posture as VramBudget.Fits on the node: no declared budget means nothing is
            // gated, because a deployment that never set Node:Vram:BudgetMiB behaves as it always
            // did rather than being blocked by a check it never opted into.
            return new ModelPrecheckResult(true, null, false, null, null,
                "this node has not declared a VRAM budget (Node:Vram:BudgetMiB), so nothing is checked");
        }

        var inventory = registry.ModelInventory();
        var resident = inventory
            .FirstOrDefault(n => string.Equals(n.NodeId, node.NodeId, StringComparison.OrdinalIgnoreCase))?
            .Models.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));

        var isEstimate = resident is null;
        var sizeBytes = resident?.SizeBytes
            ?? registry.DistinctModels()
                .FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase))?.SizeBytes;

        if (sizeBytes is not { } bytes || bytes <= 0)
        {
            // A model with no reported size anywhere in the fleet is admitted rather than guessed
            // at — inventing a number would put it behind a refusal derived from arithmetic nobody
            // wrote down (the same rule VramBudget.Evaluate applies to an unrecognised recipe).
            return new ModelPrecheckResult(true, null, false, budgetMiB, reserveMiB,
                "no reported size for this model anywhere in the fleet, so there is nothing to check it against");
        }

        var estimatedMiB = (long)Math.Ceiling(bytes / (1024.0 * 1024.0));
        var headroomMiB = budgetMiB - Math.Max(0, reserveMiB);
        var fits = headroomMiB > 0 && estimatedMiB <= headroomMiB;

        var reason = fits
            ? "fits within this node's declared VRAM budget"
            : $"'{model}' is estimated at {estimatedMiB} MiB ({(isEstimate ? "fleet-wide disk size, not measured on this node" : "this node's own reported disk size")}) and this node budgets {Math.Max(0, headroomMiB)} MiB for models (Node:Vram:BudgetMiB {budgetMiB} minus Node:Vram:ReserveMiB {reserveMiB})";

        return new ModelPrecheckResult(fits, estimatedMiB, isEstimate, budgetMiB, reserveMiB, reason);
    }

    /// <summary>
    /// Enables or disables <paramref name="model"/> on <paramref name="node"/>, going through the
    /// profile mechanism (phase 74 D2) rather than a second routing switch. Enabling runs
    /// <see cref="Precheck"/> first; a refusal is reported back rather than thrown, since a caller
    /// (human via <c>force=true</c>, or the auto-scaler never overriding at all) decides what to do
    /// with it.
    /// </summary>
    public async Task<ModelToggleOutcome> SetEnabledAsync(
        NodeSnapshot node,
        string model,
        bool enabled,
        bool force,
        string by,
        CancellationToken cancellationToken)
    {
        model = (model ?? string.Empty).Trim();

        ModelPrecheckResult? precheck = null;

        if (enabled)
        {
            precheck = Precheck(node, model);

            if (!precheck.Ok && !force)
            {
                return new ModelToggleOutcome(Applied: false, Conflict: false, ConflictError: null, Precheck: precheck, Profile: null);
            }
        }

        var (conflictError, name, existing) = ResolveProfileForNode(node);

        if (conflictError is not null)
        {
            return new ModelToggleOutcome(Applied: false, Conflict: true, ConflictError: conflictError, Precheck: precheck, Profile: null);
        }

        var models = existing.Models ?? new NodeProfileModels();
        var disabled = (models.Disabled ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Where(m => !string.Equals(m, model, StringComparison.OrdinalIgnoreCase));

        var updatedDisabled = enabled
            ? disabled.ToArray()
            : disabled.Append(model).ToArray();

        var updated = existing with { Models = models with { Disabled = updatedDisabled } };

        var stored = profiles.Put(name, updated);
        await coordinator.ReassertAsync(cancellationToken);

        audit.Record(node.NodeId, $"model.{(enabled ? "enable" : "disable")}:{model}", by, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Model '{Model}' {State} on node {NodeId} via profile '{Profile}' revision {Revision} (by {By})",
            model, enabled ? "enabled" : "disabled", node.NodeId, stored.Name, stored.Revision, by);

        return new ModelToggleOutcome(Applied: true, Conflict: false, ConflictError: null, Precheck: precheck, Profile: stored);
    }

    /// <summary>
    /// Which named profile a per-node model toggle should patch: whatever already matches the node,
    /// or a fresh <c>node:{id}</c> skeleton if none does. Mirrors <see cref="NodeProfileCoordinator.ReassertAsync"/>'s
    /// own match so a conflicted node is refused here exactly as it is refused a push.
    /// </summary>
    private (string? ConflictError, string Name, NodeProfile Profile) ResolveProfileForNode(NodeSnapshot node)
    {
        var assignment = profiles.MatchFor(node.NodeId, node.Labels);

        if (assignment.IsConflict)
        {
            return ($"node '{node.NodeId}' matches {assignment.Conflicts!.Count} profiles ({string.Join(", ", assignment.Conflicts)}); resolve the conflict in the raw profile editor before toggling a model here",
                string.Empty, null!);
        }

        if (assignment.Profile is { } existing)
        {
            return (null, existing.Name, existing);
        }

        var name = $"node:{node.NodeId}";
        return (null, name, new NodeProfile(name, 0, new NodeProfileSelector(NodeId: node.NodeId)));
    }
}

public sealed record ModelPrecheckResult(
    bool Ok,
    long? EstimatedMiB,
    bool IsEstimate,
    int? BudgetMiB,
    int? ReserveMiB,
    string Reason);

public sealed record ModelToggleOutcome(
    bool Applied,
    bool Conflict,
    string? ConflictError,
    ModelPrecheckResult? Precheck,
    NodeProfile? Profile);
