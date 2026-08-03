using System.Runtime.CompilerServices;
using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Retrieval;
using InferHub.Node.Vector;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using InferHub.Shared.Vector.Replication;

namespace InferHub.Node;

public sealed class InferenceExecutor(
    IInferenceBackend backend,
    ReplicaStore replicas,
    RetrievalHost retrieval,
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
                "vector-query" => RunVectorQuery(job.RequestJson),

                // Phase 44. Work against the corpus this node *owns*, dispatched by the hub whose API
                // the client actually called (D5). Distinct from `vector-query`, which reads a
                // phase-15 replica derived from the hub's own store — these two must never be
                // confused, because one of them writes.
                CorpusJobKinds.Ingest => await RunCorpusIngestAsync(job.RequestJson, cancellationToken),
                CorpusJobKinds.Search => await RunCorpusSearchAsync(job.RequestJson, cancellationToken),
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

    /// <summary>
    /// Ingests into the corpus this node owns, through the <b>shared</b> pipeline (D5). Chunking,
    /// deterministic ids, the stale-chunk sweep and the <c>partial</c> verdict are therefore one
    /// definition and not two — the same reason phase 38 moved the pipeline rather than rewriting it.
    /// </summary>
    /// <remarks>
    /// <b>PDF never gets here.</b> The hub refuses it with a 415 before dispatching, because
    /// <c>PdfPig</c> is coordinator-scoped by name (rule 5) and a hub that extracted the text and
    /// shipped chunks instead would be a second ingestion path with different behaviour.
    /// </remarks>
    private async Task<string> RunCorpusIngestAsync(string requestJson, CancellationToken cancellationToken)
    {
        var job = JsonSerializer.Deserialize<CorpusIngestJob>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("corpus-ingest request was empty");

        using var lease = retrieval.TryLease()
            ?? throw new InvalidOperationException("this node has no corpus running; it cannot own a collection right now");

        var result = await lease.Corpus.Ingestion.IngestAsync(
            job.Collection,
            job.Request,
            autoProvision: true,
            cancellationToken);

        return JsonSerializer.Serialize(new CorpusIngestResponse(result), JsonOptions);
    }

    private async Task<string> RunCorpusSearchAsync(string requestJson, CancellationToken cancellationToken)
    {
        var job = JsonSerializer.Deserialize<CorpusSearchJob>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("corpus-search request was empty");

        using var lease = retrieval.TryLease()
            ?? throw new InvalidOperationException("this node has no corpus running; it cannot own a collection right now");

        var matches = await lease.Corpus.Retrieval.SearchAsync(
            new RetrievalRequest(job.Collection, job.K, job.EmbeddingModel, job.Mode, job.Rerank),
            job.Query,
            job.Model,
            cancellationToken);

        return JsonSerializer.Serialize(
            new CorpusSearchResponse(matches ?? Array.Empty<VectorMatch>()),
            JsonOptions);
    }

    private string RunVectorQuery(string requestJson)
    {
        var request = JsonSerializer.Deserialize<VectorQueryRequest>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException("vector-query request was empty");

        var matches = replicas.Query(request)
            ?? throw new InvalidOperationException($"no local replica for collection '{request.Collection}'");

        return JsonSerializer.Serialize(new VectorQueryResponse(matches), JsonOptions);
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
