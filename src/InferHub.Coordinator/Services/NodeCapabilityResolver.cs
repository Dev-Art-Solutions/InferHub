using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The one place where "what did this node declare?" becomes "what may it be routed for?"
/// (phase 40, D1).
/// </summary>
/// <remarks>
/// <para>
/// A node that declares nothing — every node before v3.8, and every node whose operator has not
/// touched <c>Node:Capabilities</c> — resolves to <c>chat</c> + <c>embed</c> over every model it
/// reports, which is precisely the pre-v3.8 routing semantics. Materialising that default here,
/// once, is why no call site anywhere branches on "is this an old node".
/// </para>
/// <para>
/// A capability with no models is dropped rather than kept: it can never be routed to, and a
/// status page listing a capability that matches nothing is a claim that is not true.
/// </para>
/// </remarks>
public static class NodeCapabilityResolver
{
    public static IReadOnlyList<NodeCapability> Resolve(
        IReadOnlyList<NodeCapability>? declared,
        IReadOnlyList<ModelInfo> models)
    {
        if (declared is null)
        {
            return Default(models);
        }

        return declared
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Kind))
            .GroupBy(capability => capability.Kind.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new NodeCapability(
                group.Key,
                group
                    .SelectMany(capability => capability.Models ?? Array.Empty<string>())
                    .Where(model => !string.IsNullOrWhiteSpace(model))
                    .Select(model => model.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .Where(capability => capability.Models.Count > 0)
            .OrderBy(capability => capability.Kind, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Whether a resolved set covers a <c>(capability, model)</c> pair.</summary>
    public static bool Provides(
        IReadOnlyList<NodeCapability> resolved,
        string capability,
        string model)
    {
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var wantedKind = capability.Trim();
        var wantedModel = model.Trim();

        foreach (var declared in resolved)
        {
            if (!string.Equals(declared.Kind, wantedKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var name in declared.Models)
            {
                if (string.Equals(name, wantedModel, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlyList<NodeCapability> Default(IReadOnlyList<ModelInfo> models)
    {
        var names = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(model => model.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            return Array.Empty<NodeCapability>();
        }

        return
        [
            new NodeCapability(CapabilityKinds.Chat, names),
            new NodeCapability(CapabilityKinds.Embed, names)
        ];
    }
}
