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

        var node = router.Route(model, conversationKey: null);
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
            var scores = ParseScores(content, candidates.Count);
            if (scores is null)
            {
                logger.LogInformation("Rerank fell back to original order: could not parse scores from model output");
                return candidates;
            }

            // Stable sort by score descending: ties keep their incoming (fused) order, so a reranker
            // that scores everything equal is a no-op rather than a reshuffle.
            return candidates
                .Select((match, index) => (match, index, score: scores[index]))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.index)
                .Select(x => x.match)
                .ToArray();
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
        var prompt = BuildPrompt(query, candidates);
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

    internal static string BuildPrompt(string query, IReadOnlyList<VectorMatch> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a search reranker. Score how well each passage answers the QUESTION,");
        sb.AppendLine("from 0 (irrelevant) to 10 (directly answers it). Judge only relevance to the question.");
        sb.AppendLine("Respond with ONLY a JSON array of integers, one per passage, in order. No prose.");
        sb.AppendLine($"Example for 3 passages: [8, 2, 5]");
        sb.AppendLine();
        sb.Append("QUESTION: ").AppendLine(query);
        sb.AppendLine();
        for (var i = 0; i < candidates.Count; i++)
        {
            var text = ChunkText.Extract(candidates[i].Payload);
            sb.Append("PASSAGE ").Append(i + 1).Append(": ").AppendLine(Truncate(text, 1000));
        }
        return sb.ToString();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static string ExtractContent(string responseJson)
    {
        var response = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
        return response?.Message?.Content ?? string.Empty;
    }

    /// <summary>
    /// Parse the model's answer into one score per candidate. Tolerant on purpose: it finds the
    /// first JSON array of numbers, and if the count does not match it gives up (returns null) rather
    /// than guess — a wrong-length parse would reorder against scores that belong to other passages.
    /// </summary>
    internal static double[]? ParseScores(string content, int expected)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var start = content.IndexOf('[');
        var end = content.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        var slice = content[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(slice);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var scores = new List<double>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
                {
                    scores.Add(value);
                }
                else
                {
                    return null;
                }
            }

            return scores.Count == expected ? scores.ToArray() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
