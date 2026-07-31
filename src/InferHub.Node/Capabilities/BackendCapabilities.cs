using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Capabilities;

/// <summary>
/// What this node declares it can do (phase 40, D2). Derived from what is actually loaded — the
/// backend's model list — and narrowed by <c>Node:Capabilities:Disabled</c>.
/// </summary>
/// <remarks>
/// <para>
/// A hand-maintained capability list drifts the day somebody runs <c>ollama pull</c>, and a node
/// that <em>claims</em> a capability it does not have is worse than one that claims none: the hub
/// routes to it and the client gets the error. So the declaration follows the composition.
/// </para>
/// <para>
/// <b>Nothing here guesses what a model is for.</b> Ollama does not say, and a name-based
/// heuristic ("it has 'embed' in it") would be a capability registry that is wrong for somebody —
/// the thing phase-29 D5 refused. An operator who knows a box is for embeddings says so, once,
/// with <c>Node:Capabilities:Disabled: ["chat"]</c>.
/// </para>
/// </remarks>
public static class BackendCapabilities
{
    /// <summary>
    /// The kinds an <see cref="Backends.IInferenceBackend"/> serves. Both endpoints behind them
    /// are the backend's own, so a backend that can do one can do the other.
    /// </summary>
    private static readonly string[] BackendKinds = [CapabilityKinds.Chat, CapabilityKinds.Embed];

    public static IReadOnlyList<NodeCapability> Declare(
        IReadOnlyList<ModelInfo> models,
        CapabilityOptions options,
        IReadOnlyList<NodeCapability>? toolCapabilities = null)
    {
        var names = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(model => model.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // No models is already how a node is unrouted (phase-36 D7). Declaring backend
        // capabilities over nothing would say the same thing twice, less clearly — but a node with
        // no Ollama models and a running tool is a real deployment from phase 41 (a box that only
        // transcribes), so the tool half is folded in regardless.
        var backend = names.Length == 0
            ? Array.Empty<NodeCapability>()
            : BackendKinds
                .Where(kind => !options.IsDisabled(kind))
                .Select(kind => new NodeCapability(kind, names))
                .ToArray();

        if (toolCapabilities is not { Count: > 0 })
        {
            return backend;
        }

        // Tools declare their own kinds, and the subtractive key narrows them the same way it
        // narrows the backend's. Note that Node:Capabilities:Disabled only knows the kinds this
        // release names; the way to switch off a tool that invents its own kind is to take it out
        // of Tools:Allowed, which is the grant it needed to run in the first place.
        var merged = backend.ToDictionary(
            capability => capability.Kind,
            capability => capability.Models.ToList(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var capability in toolCapabilities.Where(c => !options.IsDisabled(c.Kind)))
        {
            if (!merged.TryGetValue(capability.Kind, out var existing))
            {
                merged[capability.Kind] = capability.Models
                    .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                continue;
            }

            foreach (var model in capability.Models.Where(m => !existing.Contains(m, StringComparer.OrdinalIgnoreCase)))
            {
                existing.Add(model);
            }

            existing.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return merged
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new NodeCapability(pair.Key, pair.Value))
            .ToArray();
    }
}
