using InferHub.Shared.Contracts;
using System.Threading.Channels;

namespace InferHub.Coordinator.Services;

public interface IDispatcher
{
    Task<InferenceResult> DispatchAsync(RoutableNode node, InferenceJob job, CancellationToken cancellationToken);

    Task<ChannelReader<InferenceChunk>> DispatchStreamAsync(
        RoutableNode node,
        InferenceJob job,
        CancellationToken cancellationToken);

    bool Complete(InferenceResult result);

    bool WriteChunk(InferenceChunk chunk);

    void FailForConnection(string connectionId, Exception? exception);

    /// <summary>Phase 150: a node reports on a followed job. False when nobody follows that job on that connection.</summary>
    bool ReportProgress(string connectionId, InferenceJobProgress progress) => false;
}
