using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// The one reranker v2.6 ships: it hands the query and the candidate chunks to a chat model already
/// running on the fleet with a scoring prompt, and reorders by the scores it returns. It costs one
/// round trip and is honest about that — off unless a request asks for it.
/// <para>
/// Rule 7 holds: the query and candidate text pass through in flight to the node and nothing is
/// retained here. And every failure mode — no node, timeout, unparseable answer — returns the
/// candidates untouched, because a reranker that can break retrieval is worse than none.
/// </para>
/// </summary>
internal sealed class LlmReranker(
    Services.IRouter router,
    IDispatcher dispatcher,
    IOptions<VectorStoreOptions> options,
    ILogger<LlmReranker> logger) : IReranker
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

        var node = router.Route(model, conversationKey: null, capability: CapabilityKinds.Chat);
        if (node is null)
        {
            logger.LogInformation("Rerank skipped: no node holds model '{Model}'; keeping original order", model);
            return candidates;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.Retrieval.RerankTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var body = BuildRequestJson(model!, query, candidates);
            var job = new InferenceJob(Guid.NewGuid(), "chat", body);
            var result = await dispatcher.DispatchAsync(node, job, cts.Token);

            if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
            {
                logger.LogInformation("Rerank fell back to original order: node returned {Error}", result.Error ?? "no content");
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
        var prompt = RerankPrompt.Build(query, candidates);
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Messages =
            [
                new ChatMessage { Role = "user", Content = prompt }
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
