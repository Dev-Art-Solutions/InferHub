using System.Text;

namespace InferHub.Shared.Vector.Qdrant;

/// <summary>
/// Turns chunk (or query) text into a Qdrant sparse vector — the <em>lexical</em> view of the text,
/// as (term-index, term-frequency) pairs. This is the sparse half of Qdrant-native hybrid search
/// (phase 34): a dense embedding and this sparse vector are fused server-side by the Query API.
/// <para>
/// The tokens are exactly <see cref="InvertedIndex.Tokenize"/>'s, so "the lexical view of a chunk"
/// means the same thing under the local BM25 index and under Qdrant — a query that finds an error
/// code with <c>local</c> finds it with <c>qdrant</c>. Term indices are a stable 32-bit hash of the
/// token (FNV-1a over its UTF-8 bytes), deterministic across processes and platforms.
/// </para>
/// <para>
/// Values are raw term frequencies; the IDF weighting is applied by Qdrant <b>server-side</b> — the
/// collection's sparse vector is declared with <c>modifier: idf</c>, so Qdrant multiplies each term
/// by an inverse-document-frequency it computes from its own corpus. That keeps this zero-dependency
/// (no sparse-embedding model, no corpus statistics threaded through the hub) and is why a rare
/// hash collision merely conflates two terms — acceptable and honest for a lexical branch.
/// </para>
/// Pure and deterministic: same text → same vector.
/// </summary>
public static class SparseVector
{
    /// <summary>The sparse vector for <paramref name="text"/>, or null when it has no lexical terms
    /// (an empty string, or a payload with no text to index — the same "nothing to rank" stance
    /// <see cref="ChunkText"/> takes).</summary>
    public static QdrantSparse? Build(string? text)
    {
        var freqs = new Dictionary<uint, float>();
        foreach (var token in InvertedIndex.Tokenize(text))
        {
            var index = HashTerm(token);
            freqs[index] = freqs.TryGetValue(index, out var f) ? f + 1f : 1f;
        }

        if (freqs.Count == 0)
        {
            return null;
        }

        var indices = new uint[freqs.Count];
        var values = new float[freqs.Count];
        var i = 0;
        foreach (var (index, value) in freqs)
        {
            indices[i] = index;
            values[i] = value;
            i++;
        }
        return new QdrantSparse(indices, values);
    }

    /// <summary>FNV-1a 32-bit over the token's UTF-8 bytes. The token is already lowercased by the
    /// tokenizer, so casing is folded before it reaches here.</summary>
    public static uint HashTerm(string token)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        var hash = offset;
        var bytes = Encoding.UTF8.GetBytes(token);
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
