using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

public interface IInferenceBackend
{
    string Name { get; }

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken);

    Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken);
}
