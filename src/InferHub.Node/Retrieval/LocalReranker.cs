using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Retrieval;

/// <summary>
/// Solo mode's reranker (phase-38 D8): the hub's <c>LlmReranker</c> with the routing removed. The
/// prompt and the score parser are the shared ones (<see cref="RerankPrompt"/>), so a corpus ranks
/// the same whichever host is in front of it.
/// </summary>
/// <remarks>
/// Phase-24 D4 survives verbatim and matters more here than on the hub: <b>every</b> failure —
/// no model, a timeout, prose instead of an array, an array of the wrong length — returns the
/// candidates untouched. A solo box is one wedged Ollama away from every rerank failing, and a
/// reranker that can break retrieval is worse than no reranker.
/// </remarks>
public sealed class LocalReranker(
    InferenceExecutor executor,
    IOptions<LocalRetrievalOptions> options,
    ILogger<LocalReranker> logger) : IReranker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IReadOnlyList<VectorMatch>> RerankAsync(
        string query,
        IReadOnlyList<VectorMatch> candidates,
        string? model,
        CancellationToken cancellationToken)
    {
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            logger.LogInformation("Rerank skipped: no rerank model resolved; keeping original order");
            return candidates;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.Retrieval.RerankTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var job = new InferenceJob(Guid.NewGuid(), "chat", BuildRequestJson(model!, query, candidates));
            var result = await executor.RunAsync(job, cts.Token);

            if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
            {
                logger.LogInformation(
                    "Rerank fell back to original order: backend returned {Error}",
                    NodeErrorText.Readable(result.Error) ?? "no content");
                return candidates;
            }

            var content = ExtractContent(result.ResponseJson);
            var scores = RerankPrompt.ParseScores(content, candidates.Count);
            if (scores is null)
            {
                logger.LogInformation("Rerank fell back to original order: could not parse scores from model output");
                return candidates;
            }

            return RerankPrompt.Apply(candidates, scores);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired, not the caller's cancellation. Original order stands.
            logger.LogInformation("Rerank timed out after {Timeout}s; keeping original order", timeout.TotalSeconds);
            return candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rerank failed; keeping original order");
            return candidates;
        }
    }

    private static string BuildRequestJson(string model, string query, IReadOnlyList<VectorMatch> candidates)
    {
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Messages =
            [
                new ChatMessage { Role = "user", Content = RerankPrompt.Build(query, candidates) }
            ],
            Options = JsonSerializer.SerializeToElement(new { temperature = 0 })
        };
        return JsonSerializer.Serialize(request, JsonOptions);
    }

    private static string ExtractContent(string responseJson)
    {
        var response = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
        return response?.Message?.Content ?? string.Empty;
    }
}
