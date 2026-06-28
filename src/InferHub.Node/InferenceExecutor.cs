using System.Runtime.CompilerServices;
using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Shared.Contracts;

namespace InferHub.Node;

public sealed class InferenceExecutor(
    IInferenceBackend backend,
    ILogger<InferenceExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<InferenceResult> RunAsync(InferenceJob job, CancellationToken cancellationToken)
    {
        try
        {
            var responseJson = job.Kind switch
            {
                "generate" => await backend.GenerateAsync(job.RequestJson, cancellationToken),
                "chat" => await backend.ChatAsync(job.RequestJson, cancellationToken),
                "embed" => await backend.EmbedAsync(job.RequestJson, cancellationToken),
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

    public async IAsyncEnumerable<InferenceChunk> StreamAsync(
        InferenceJob job,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sentFinalChunk = false;
        var chunks = backend
            .StreamAsync(job.Kind, job.RequestJson, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                string? responseJson = null;
                Exception? error = null;
                var hasNext = false;

                try
                {
                    hasNext = await chunks.MoveNextAsync();

                    if (hasNext)
                    {
                        responseJson = chunks.Current;
                    }
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Streaming inference job {JobId} was canceled", job.JobId);
                    throw;
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                if (error is not null)
                {
                    logger.LogWarning(error, "Streaming inference job {JobId} failed", job.JobId);
                    yield return new InferenceChunk(job.JobId, SerializeError(error.Message), true);
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                var done = IsDone(responseJson!);
                sentFinalChunk = done;

                yield return new InferenceChunk(job.JobId, responseJson!, done);

                if (done)
                {
                    logger.LogInformation("Completed streaming {JobKind} job {JobId}", job.Kind, job.JobId);
                    yield break;
                }
            }
        }
        finally
        {
            await chunks.DisposeAsync();
        }

        if (!sentFinalChunk)
        {
            logger.LogWarning("Streaming {JobKind} job {JobId} ended without a done chunk", job.Kind, job.JobId);
            yield return new InferenceChunk(job.JobId, SerializeDone(), true);
        }
    }

    private static string SerializeError(string message)
    {
        return JsonSerializer.Serialize(new { error = message, done = true }, JsonOptions);
    }

    private static bool IsDone(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            return document.RootElement.TryGetProperty("done", out var done)
                && done.ValueKind is JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SerializeDone()
    {
        return JsonSerializer.Serialize(new { done = true }, JsonOptions);
    }
}
