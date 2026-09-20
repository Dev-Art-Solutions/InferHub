using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Phase 80's second <see cref="IReranker"/>: a dedicated cross-encoder tool worker instead of a
/// chat model prompted to score. Same shape as <see cref="LlmReranker"/> — route, dispatch, apply
/// scores, fall back to the original order on anything that goes wrong — swapping the chat round
/// trip for a <see cref="ToolJob"/> against <see cref="CapabilityKinds.Rerank"/>.
/// <para>
/// Rule 7 holds here exactly as it does in <see cref="LlmReranker"/>: the query and candidate text
/// pass through to the node and nothing is retained on the hub. Every failure mode — no node, a
/// timeout, a worker that does not recognise the model, a wrong-length score array — returns the
/// candidates untouched, because a reranker that can break retrieval is worse than none.
/// </para>
/// </summary>
internal sealed class CrossEncoderReranker(
    Services.IRouter router,
    IToolDispatcher tools,
    IOptions<VectorStoreOptions> options,
    ILogger<CrossEncoderReranker> logger) : IReranker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        var node = router.Route(model, conversationKey: null, capability: CapabilityKinds.Rerank);
        if (node is null)
        {
            logger.LogInformation(
                "Rerank skipped: no node currently provides '{Capability}' for model '{Model}'; keeping original order. " +
                "Retrieval:RerankModel must name a model the cross-encoder tool actually serves, not a chat model.",
                CapabilityKinds.Rerank,
                model);
            return candidates;
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.Retrieval.RerankTimeoutSeconds));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Rerank, model, BuildPayload(query, candidates));
            var result = await tools.DispatchToolAsync(node, job, cts.Token);

            if (!result.Success || string.IsNullOrEmpty(result.Payload))
            {
                logger.LogInformation("Rerank fell back to original order: node returned {Error}", result.Error ?? "no content");
                return candidates;
            }

            var scores = ParseScores(result.Payload, candidates.Count);
            if (scores is null)
            {
                logger.LogInformation("Rerank fell back to original order: could not parse scores from worker output");
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

    private static string BuildPayload(string query, IReadOnlyList<VectorMatch> candidates)
    {
        var documents = candidates.Select(c => ChunkText.Extract(c.Payload) ?? string.Empty).ToArray();
        return JsonSerializer.Serialize(new { query, documents }, JsonOptions);
    }

    /// <summary>
    /// The worker's answer is <c>{"scores": [...]}</c>, one float per document in the order sent —
    /// no free text to hunt through, unlike <see cref="RerankPrompt.ParseScores"/>'s chat-completion
    /// case. Still exact-length or nothing, for the same reason: a wrong-length array would reorder
    /// against scores that belong to other passages.
    /// </summary>
    private static double[]? ParseScores(string payloadJson, int expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (!doc.RootElement.TryGetProperty("scores", out var scoresElement)
                || scoresElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var scores = new List<double>();
            foreach (var element in scoresElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
                {
                    return null;
                }

                scores.Add(value);
            }

            return scores.Count == expected ? scores.ToArray() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
