using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Retrieval;

/// <summary>
/// Solo mode's half of phase 80: <see cref="Coordinator.Vector.CrossEncoderReranker"/> with the
/// routing removed, the same relationship <see cref="LocalReranker"/> already has to
/// <c>LlmReranker</c>. Dispatches through <see cref="ToolExecutor"/> rather than
/// <c>InferenceExecutor</c> — a cross-encoder is a tool worker, not a chat model.
/// </summary>
public sealed class LocalCrossEncoderReranker(
    ToolExecutor executor,
    IOptions<LocalRetrievalOptions> options,
    ILogger<LocalCrossEncoderReranker> logger) : IReranker
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

        if (!executor.Provides(CapabilityKinds.Rerank, model))
        {
            logger.LogInformation(
                "Rerank skipped: this node does not provide '{Capability}' for model '{Model}'; keeping original order. " +
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
            var result = await executor.RunAsync(job, progress: null, cts.Token);

            if (!result.Success || string.IsNullOrEmpty(result.Payload))
            {
                logger.LogInformation(
                    "Rerank fell back to original order: backend returned {Error}",
                    NodeErrorText.Readable(result.Error) ?? "no content");
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
