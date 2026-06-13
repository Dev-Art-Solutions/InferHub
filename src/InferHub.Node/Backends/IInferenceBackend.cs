using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends;

public interface IInferenceBackend
{
    string Name { get; }

    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}
