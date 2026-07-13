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
}
