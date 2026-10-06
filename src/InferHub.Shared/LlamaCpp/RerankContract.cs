using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Shared.LlamaCpp;

/// <summary>
/// <c>POST /v1/rerank</c> (phase 96, D5): the Jina/Cohere shape llama.cpp's server also speaks, in
/// front of phase 80's <c>rerank</c> job — <c>{query, documents}</c> in, <c>{scores}</c> out — so a
/// cross-encoder tool worker and a llama.cpp reranker answer the same request.
/// </summary>
/// <remarks>
/// Rule 7: a query and its documents are content. Nothing here is logged; the edge logs the model,
/// the count and the outcome.
/// </remarks>
public sealed record RerankRequest(string Model, string Query, IReadOnlyList<string> Documents, int? TopN, bool ReturnDocuments)
{
    public static RerankRequest? TryParse(string raw, out string error)
    {
        error = "";

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "request body must be a JSON object";
                return null;
            }

            if (Text(root, "model") is not { Length: > 0 } model)
            {
                error = "model is required";
                return null;
            }

            if (Text(root, "query") is not { Length: > 0 } query)
            {
                error = "query is required";
                return null;
            }

            if (!root.TryGetProperty("documents", out var documents)
                || documents.ValueKind != JsonValueKind.Array
                || documents.GetArrayLength() == 0)
            {
                error = "documents must be a non-empty array";
                return null;
            }

            var texts = new List<string>();

            foreach (var item in documents.EnumerateArray())
            {
                // Cohere also accepts {"text": "..."}; both are the same document.
                var text = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString(),
                    _ => null
                };

                if (text is null)
                {
                    error = "each document must be a string or an object with a string 'text'";
                    return null;
                }

                texts.Add(text);
            }

            int? topN = null;

            if (root.TryGetProperty("top_n", out var top) && top.ValueKind != JsonValueKind.Null)
            {
                if (!top.TryGetInt32(out var n) || n < 1)
                {
                    error = "top_n must be a positive integer";
                    return null;
                }

                topN = n;
            }

            var returnDocuments = root.TryGetProperty("return_documents", out var ret) && ret.ValueKind == JsonValueKind.True;

            return new RerankRequest(model.Trim(), query, texts, topN, returnDocuments);
        }
        catch (JsonException)
        {
            error = "request body must be a JSON object";
            return null;
        }
    }

    /// <summary>Phase 80's job payload, unchanged, so every <c>rerank</c> provider reads it.</summary>
    public string JobPayload()
        => JsonSerializer.Serialize(new { query = Query, documents = Documents });

    /// <summary>
    /// The worker's <c>{"scores": [...]}</c> as results, best first, cut at <c>top_n</c>. Null when the
    /// answer is not one score per document — reordering against somebody else's scores is worse
    /// than failing (80's exact-length rule).
    /// </summary>
    public string? Render(string payload)
    {
        double[] scores;
        long tokens = 0;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("scores", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            scores = array.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : double.NaN)
                .ToArray();

            if (root.TryGetProperty("total_tokens", out var t) && t.TryGetInt64(out var n))
            {
                tokens = n;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (scores.Length != Documents.Count || scores.Any(double.IsNaN))
        {
            return null;
        }

        var results = new JsonArray();

        foreach (var (score, index) in scores
                     .Select((score, index) => (score, index))
                     .OrderByDescending(pair => pair.score)
                     .ThenBy(pair => pair.index)
                     .Take(TopN ?? scores.Length))
        {
            var result = new JsonObject { ["index"] = index, ["relevance_score"] = score };

            if (ReturnDocuments)
            {
                result["document"] = new JsonObject { ["text"] = Documents[index] };
            }

            results.Add(result);
        }

        return new JsonObject
        {
            ["object"] = "list",
            ["model"] = Model,
            ["results"] = results,
            ["usage"] = new JsonObject { ["prompt_tokens"] = tokens, ["total_tokens"] = tokens }
        }.ToJsonString();
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
