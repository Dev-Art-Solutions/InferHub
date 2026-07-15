using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

/// <summary>How a retrieval request searches the collection. Per-request via
/// <c>X-InferHub-Retrieve-Mode</c>, defaulting to <see cref="Vector"/> so every deployment that
/// sends no header behaves exactly as it did before v2.6.</summary>
internal enum RetrievalMode
{
    /// <summary>Dense vector (embedding) search only — the pre-v2.6 behaviour.</summary>
    Vector,
    /// <summary>Lexical BM25 / full-text search only.</summary>
    Keyword,
    /// <summary>Both branches, fused by Reciprocal Rank Fusion.</summary>
    Hybrid
}

internal static class RetrievalModes
{
    public static bool TryParse(string? value, out RetrievalMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "vector": mode = RetrievalMode.Vector; return true;
            case "keyword": mode = RetrievalMode.Keyword; return true;
            case "hybrid": mode = RetrievalMode.Hybrid; return true;
            default: mode = RetrievalMode.Vector; return false;
        }
    }

    public static string ToWire(RetrievalMode mode) => mode switch
    {
        RetrievalMode.Keyword => "keyword",
        RetrievalMode.Hybrid => "hybrid",
        _ => "vector"
    };
}

/// <summary>
/// Reciprocal Rank Fusion. Given the vector and keyword result lists — each already ordered
/// best-first — it combines them by <em>rank</em>: a record's fused score is
/// <c>Σ 1 / (k + rank)</c> across the lists it appears in (rank is 1-based, <c>k = 60</c>).
/// <para>
/// Fusing by rank rather than by score is the whole point. A cosine distance and a BM25 score live
/// on different scales that no fixed constant reconciles across corpora; normalising them is a
/// corpus-specific guess dressed up as sophistication. RRF needs no normalisation, has one tunable,
/// and fails in ways you can reason about.
/// </para>
/// </summary>
internal static class HybridSearch
{
    public const int RrfK = 60;

    public static IReadOnlyList<VectorMatch> Fuse(
        IReadOnlyList<VectorMatch> vector,
        IReadOnlyList<VectorMatch> keyword,
        int k)
    {
        if (k < 1) return Array.Empty<VectorMatch>();

        var fused = new Dictionary<string, double>(StringComparer.Ordinal);
        var records = new Dictionary<string, VectorMatch>(StringComparer.Ordinal);

        Accumulate(vector, fused, records);
        Accumulate(keyword, fused, records);

        return fused
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(k)
            // The fused RRF score replaces the branch's native score: a caller reading .Score off a
            // hybrid result is reading the fusion rank strength, not a stray cosine or BM25 number.
            .Select(pair => records[pair.Key] with { Score = pair.Value })
            .ToArray();
    }

    private static void Accumulate(
        IReadOnlyList<VectorMatch> list,
        Dictionary<string, double> fused,
        Dictionary<string, VectorMatch> records)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var match = list[i];
            var contribution = 1.0 / (RrfK + i + 1);
            fused[match.Id] = fused.TryGetValue(match.Id, out var running) ? running + contribution : contribution;
            records.TryAdd(match.Id, match);
        }
    }
}
