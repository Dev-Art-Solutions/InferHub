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
    /// <param name="backendKinds">
    /// What the backend says it serves — <see cref="Backends.IInferenceBackend.Kinds"/> (phase 67).
    /// It was a constant <c>[chat, embed]</c> here until then, on the reasoning that both endpoints
    /// behind them are the backend's own so a backend that can do one can do the other. **A cloud
    /// vendor broke that**: Anthropic publishes no embeddings API, and this file is the last place
    /// that should be deciding so — the whole point below is that nothing here guesses what a model
    /// is for (40 D2). So the backend declares it and this narrows it.
    /// </param>
    /// <param name="narrowed">
    /// Capability kinds a coordinator profile switched off (phase 43). It is applied on top of
    /// <c>Node:Capabilities:Disabled</c> and can only ever be a superset of it, because the clamp
    /// that produced it started from that list and refuses to remove anything from it.
    /// </param>
    public static IReadOnlyList<NodeCapability> Declare(
        IReadOnlyList<ModelInfo> models,
        IReadOnlyList<string> backendKinds,
        CapabilityOptions options,
        IReadOnlyList<NodeCapability>? toolCapabilities = null,
        IReadOnlyList<string>? narrowed = null)
    {
        var disabled = narrowed is { Count: > 0 } ? narrowed : options.Disabled;

        bool IsDisabled(string kind) =>
            disabled.Any(entry => string.Equals(entry?.Trim(), kind, StringComparison.OrdinalIgnoreCase));

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
            : backendKinds
                .Where(kind => !IsDisabled(kind))
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

        foreach (var capability in toolCapabilities.Where(c => !IsDisabled(c.Kind)))
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
