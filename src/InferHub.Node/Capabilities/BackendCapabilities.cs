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
        CapabilityOptions options)
    {
        var names = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Name))
            .Select(model => model.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            // No models is already how a node is unrouted (phase-36 D7). Declaring capabilities
            // over nothing would say the same thing twice, less clearly.
            return Array.Empty<NodeCapability>();
        }

        return BackendKinds
            .Where(kind => !options.IsDisabled(kind))
            .Select(kind => new NodeCapability(kind, names))
            .ToArray();
    }
}
