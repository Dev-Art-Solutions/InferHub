using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Services;

public interface IDispatcher
{
    Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken);

    bool Complete(InferenceResult result);
}
