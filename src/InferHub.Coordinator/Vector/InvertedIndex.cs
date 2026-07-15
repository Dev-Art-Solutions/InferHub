namespace InferHub.Coordinator.Vector;

/// <summary>
/// A small in-memory BM25 inverted index over a collection's chunk text — the keyword half of
/// hybrid retrieval. It sits next to the <c>FlatIndex</c> in every <see cref="LocalVectorStore"/>
/// collection and is, like the <c>FlatIndex</c>, <b>derived, never authoritative</b>: it is rebuilt
/// from the raw store on startup and updated on every upsert and delete, and if it were lost the
/// vectors would still be intact. Pulling in Lucene to rank a few thousand chunks would be the wrong
/// trade; BM25 over a dictionary is a hundred lines and exact.
/// <para>
/// Not internally synchronised — callers hold the collection's write lock, exactly as they already
/// do around the <c>FlatIndex</c>, so the two indexes never diverge under a concurrent upsert.
/// </para>
/// </summary>
internal sealed class InvertedIndex
{
    // Okapi BM25 defaults. k1 controls term-frequency saturation, b controls length normalisation;
    // 1.2 / 0.75 are the values the literature has converged on and are what the brief specifies.
    private const double K1 = 1.2;
    private const double B = 0.75;

    // term -> (docId -> term frequency in that doc)
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    // docId -> its per-term frequencies, kept so a re-index or delete can subtract the old document
    // exactly rather than re-tokenising text we may no longer hold.
    private readonly Dictionary<string, Dictionary<string, int>> _docTerms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _docLengths = new(StringComparer.Ordinal);
    private long _totalLength;

    public int DocumentCount => _docLengths.Count;

    /// <summary>Index (or re-index) a document's text under <paramref name="id"/>. Empty text removes it.</summary>
    public void Index(string id, string? text)
    {
        Remove(id);

        var freqs = CountTerms(text);
        if (freqs.Count == 0)
        {
            return;
        }

        var length = 0;
        foreach (var (term, tf) in freqs)
        {
            length += tf;
            if (!_postings.TryGetValue(term, out var docs))
            {
                docs = new Dictionary<string, int>(StringComparer.Ordinal);
                _postings[term] = docs;
            }
            docs[id] = tf;
        }

        _docTerms[id] = freqs;
        _docLengths[id] = length;
        _totalLength += length;
    }

    public bool Remove(string id)
    {
        if (!_docTerms.TryGetValue(id, out var freqs))
        {
            return false;
        }

        foreach (var term in freqs.Keys)
        {
            if (_postings.TryGetValue(term, out var docs))
            {
                docs.Remove(id);
                if (docs.Count == 0)
                {
                    _postings.Remove(term);
                }
            }
        }

        _totalLength -= _docLengths[id];
        _docLengths.Remove(id);
        _docTerms.Remove(id);
        return true;
    }

    /// <summary>Top <paramref name="k"/> document ids by BM25 score for <paramref name="query"/>, best first.</summary>
    public IReadOnlyList<KeywordHit> Search(string? query, int k)
    {
        if (k < 1)
        {
            return Array.Empty<KeywordHit>();
        }

        var n = _docLengths.Count;
        if (n == 0)
        {
            return Array.Empty<KeywordHit>();
        }

        var queryTerms = CountTerms(query);
        if (queryTerms.Count == 0)
        {
            return Array.Empty<KeywordHit>();
        }

        var avgdl = _totalLength / (double)n;
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var term in queryTerms.Keys)
        {
            if (!_postings.TryGetValue(term, out var docs))
            {
                continue;
            }

            var df = docs.Count;
            // BM25 idf with the +0.5 smoothing; the +1 inside the log keeps it non-negative even
            // for a term that appears in more than half the corpus.
            var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));

            foreach (var (docId, tf) in docs)
            {
                var dl = _docLengths[docId];
                var denom = tf + K1 * (1 - B + B * dl / avgdl);
                var contribution = idf * (tf * (K1 + 1)) / denom;
                scores[docId] = scores.TryGetValue(docId, out var running) ? running + contribution : contribution;
            }
        }

        return scores
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(k)
            .Select(pair => new KeywordHit(pair.Key, pair.Value))
            .ToArray();
    }

    private static Dictionary<string, int> CountTerms(string? text)
    {
        var freqs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in Tokenize(text))
        {
            freqs[token] = freqs.TryGetValue(token, out var current) ? current + 1 : 1;
        }
        return freqs;
    }

    /// <summary>
    /// Lowercase, split on any run of non-alphanumeric characters. Deliberately dumb: no stemming,
    /// no stopword list. An error code like <c>E-4021</c> becomes <c>e</c> and <c>4021</c>, and it is
    /// the rare <c>4021</c> that carries the high idf and pulls the exact chunk to the top — which is
    /// the whole reason keyword search exists alongside the embeddings.
    /// </summary>
    public static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return text[start..].ToLowerInvariant();
        }
    }
}

internal readonly record struct KeywordHit(string Id, double Score);
