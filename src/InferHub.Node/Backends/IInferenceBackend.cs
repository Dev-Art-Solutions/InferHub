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

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

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
