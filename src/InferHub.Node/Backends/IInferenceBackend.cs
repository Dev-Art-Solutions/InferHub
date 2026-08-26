using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

public interface IInferenceBackend
{
    string Name { get; }

    /// <summary>
    /// Where this backend actually runs. Reported at registration and shown on the status page —
    /// before phase 22 the node just sent <c>Ollama:Endpoint</c>, which an OpenAI-backed node
    /// would have reported as <c>localhost:11434</c> while talking to something else entirely.
    /// </summary>
    string Endpoint { get; }

    /// <summary>
    /// The capability kinds this backend serves (phase 67). <b>Declared, not discovered</b> — the
    /// same argument <see cref="SupportsModelManagement"/> makes one member down, for the same
    /// reason: Anthropic publishes no embeddings API, and a node that declared <c>embed</c> anyway
    /// would have the hub route an embedding job to it and the client read a 501 *after* the hop.
    /// Phase 40's router already has a 503 that names the missing capability, before it.
    /// </summary>
    /// <remarks>
    /// It is not derived from the model list and it is not derived from a vendor name inside
    /// <c>BackendCapabilities</c> — that file's whole point (40 D2) is that nothing there guesses
    /// what a model is for. <c>Node:Capabilities:Disabled</c> stays subtractive over the result.
    /// </remarks>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>
    /// What this backend holds, or <b>null when it could not be asked</b> (phase 69, v3.36.2).
    /// </summary>
    /// <remarks>
    /// The distinction is phase-23 D1's, one project over: <em>"not fetched" must not be confusable
    /// with "not there"</em>. Both used to come back as an empty list, so the node reported a
    /// failure to the coordinator <b>as data</b> — and a hub cannot tell an unreachable server from
    /// a box whose weights were deleted. That is what turned v3.36's <c>503</c> naming the backend
    /// back into a <c>404 model not found</c> one refresh interval later.
    /// Callers that only want to render a list say <c>?? []</c>; the one caller that is *reporting*
    /// must not.
    /// </remarks>
    Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken);

    Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken);

    Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken);

    Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this backend can pull, delete and warm models (phase 26). Ollama can; a vLLM or other
    /// OpenAI-compatible upstream has its model fixed at launch and cannot, so it returns
    /// <c>false</c> and the coordinator never offers the controls. A backend that throws when asked
    /// to do the impossible is a seam nobody trusts twice — so the capability is declared, not
    /// discovered by exception.
    /// </summary>
    bool SupportsModelManagement { get; }

    /// <summary>Download a model, streaming progress. Only called when <see cref="SupportsModelManagement"/>.</summary>
    IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken);

    /// <summary>Delete a model. Only called when <see cref="SupportsModelManagement"/>.</summary>
    Task DeleteAsync(string model, CancellationToken cancellationToken);

    /// <summary>Load a model into memory so the first real request does not pay the cold-start cost.</summary>
    Task WarmAsync(string model, CancellationToken cancellationToken);
}

/// <summary>
/// A backend-agnostic pull progress frame. The node's <see cref="ModelCommandExecutor"/> maps these
/// onto <see cref="InferHub.Shared.Contracts.ModelCommandProgress"/> — the backend does not know the
/// command id or the node id, so it does not carry them.
/// </summary>
public sealed record ModelPullProgress(string Status, long? Total, long? Completed);
