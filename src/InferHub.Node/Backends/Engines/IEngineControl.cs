using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

/// <summary>
/// The engines on a <c>Backend:Engines</c> node, as something that can be started and stopped
/// (phase 95). Registered only on such a node — a single-backend node has none, so the profile
/// applier and the connection hold a nullable rather than a stand-in that refuses everything.
/// </summary>
public interface IEngineControl
{
    /// <summary>The configured names, sorted (as configuration binds them). The ceiling a profile cannot raise.</summary>
    IReadOnlyList<string> EngineNames { get; }

    /// <summary>
    /// Converges the running set on each engine's <c>Autostart</c> overlaid by
    /// <paramref name="overrides"/> (already clamped). Returns one line per engine whose state changed.
    /// </summary>
    Task<IReadOnlyList<string>> ApplyAsync(IReadOnlyDictionary<string, bool>? overrides, CancellationToken cancellationToken);

    NodeBackendState State(string nodeId);

    /// <summary>An engine started answering, stopped, or was stopped: what this node serves changed.</summary>
    event Action? Changed;
}

/// <summary>
/// A backend whose models do not all serve the same kinds (phase 95): an Ollama model chats and
/// embeds, a colibri one chats and scores, an embedding <c>llama-server</c> only embeds. The
/// declaration asks per model rather than multiplying every kind by every name.
/// </summary>
public interface IModelKinds
{
    /// <summary>The kinds <paramref name="model"/> is served under, or null when it is not known here.</summary>
    IReadOnlyList<string>? KindsFor(string model);
}
