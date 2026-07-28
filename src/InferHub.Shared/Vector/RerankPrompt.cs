using System.Text;
using System.Text.Json;

namespace InferHub.Shared.Vector;

/// <summary>
/// The reranker's prompt and its answer parser — the two pure halves of LLM reranking, shared by
/// the hub's <c>LlmReranker</c> (which dispatches on the fleet) and the node's <c>LocalReranker</c>
/// (which runs on its own backend). Phase-38 D8.
/// </summary>
/// <remarks>
/// What is shared is what turns a candidate set into <em>text</em> and a model's answer back into
/// scores — the phase-37 D6 line, applied again. Two rerank prompts that drifted would give the
/// same corpus two different rankings depending on where it was hosted, and nothing would look
/// broken.
/// </remarks>
public static class RerankPrompt
{
    public static string Build(string query, IReadOnlyList<VectorMatch> candidates)
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

    /// <summary>
    /// Parse the model's answer into one score per candidate. Tolerant on purpose: it finds the
    /// first JSON array of numbers, and if the count does not match it gives up (returns null)
    /// rather than guess — a wrong-length parse would reorder against scores that belong to other
    /// passages.
    /// </summary>
    public static double[]? ParseScores(string content, int expected)
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

    /// <summary>
    /// Apply parsed scores to a candidate list. A <b>stable</b> sort by score descending: ties keep
    /// their incoming (fused) order, so a model that scores everything equal is a no-op rather than
    /// a reshuffle.
    /// </summary>
    public static IReadOnlyList<VectorMatch> Apply(IReadOnlyList<VectorMatch> candidates, double[] scores) =>
        candidates
            .Select((match, index) => (match, index, score: scores[index]))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.match)
            .ToArray();
}
