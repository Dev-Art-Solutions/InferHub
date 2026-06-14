using InferHub.Node.Backends;
using InferHub.Shared.Contracts;

namespace InferHub.Node;

public sealed class InferenceExecutor(
    IInferenceBackend backend,
    ILogger<InferenceExecutor> logger)
{
    public async Task<InferenceResult> RunAsync(InferenceJob job, CancellationToken cancellationToken)
    {
        try
        {
            var responseJson = job.Kind switch
            {
                "generate" => await backend.GenerateAsync(job.RequestJson, cancellationToken),
                "chat" => await backend.ChatAsync(job.RequestJson, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported inference job kind '{job.Kind}'.")
            };

            logger.LogInformation("Completed {JobKind} job {JobId}", job.Kind, job.JobId);
            return InferenceResult.Succeeded(job.JobId, responseJson);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Inference job {JobId} was canceled", job.JobId);
            return InferenceResult.Failed(job.JobId, "inference job was canceled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Inference job {JobId} failed", job.JobId);
            return InferenceResult.Failed(job.JobId, ex.Message);
        }
    }
}
