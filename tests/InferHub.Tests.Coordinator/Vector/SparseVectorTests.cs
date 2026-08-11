using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Qdrant;

namespace InferHub.Tests.Vector;

/// <summary>
/// The hub-computed sparse (lexical) vector, no server. What matters: it is deterministic, it uses
/// the <em>same</em> tokenizer as the local keyword path (so "the lexical view of a chunk" means the
/// same thing under both engines), and it collapses empty text to nothing to rank.
/// </summary>
public class SparseVectorTests
{
    [Fact]
    public void SameTextProducesTheSameVector()
    {
        var a = SparseVector.Build("Error E-4021 indicates a checksum mismatch.");
        var b = SparseVector.Build("Error E-4021 indicates a checksum mismatch.");

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Indices, b!.Indices);
        Assert.Equal(a.Values, b.Values);
    }

    [Fact]
    public void IndicesAndFrequenciesMatchTheLocalKeywordTokenizer()
    {
        const string text = "checksum error checksum error error";
        var sparse = SparseVector.Build(text);
        Assert.NotNull(sparse);

        // Build the expected (index -> frequency) map straight from InvertedIndex's tokenizer: this is
        // the parity that makes local and qdrant agree on the lexical view of a chunk.
        var expected = new Dictionary<uint, float>();
        foreach (var token in InvertedIndex.Tokenize(text))
        {
            var index = SparseVector.HashTerm(token);
            expected[index] = expected.TryGetValue(index, out var f) ? f + 1f : 1f;
        }

        var actual = sparse!.Indices.Zip(sparse.Values).ToDictionary(p => p.First, p => p.Second);
        Assert.Equal(expected, actual);

        // "error" appears three times, "checksum" twice — raw term frequencies (IDF is Qdrant's job).
        Assert.Equal(3f, actual[SparseVector.HashTerm("error")]);
        Assert.Equal(2f, actual[SparseVector.HashTerm("checksum")]);
    }

    [Fact]
    public void CasingIsFoldedExactlyAsTheTokenizerFoldsIt()
    {
        var upper = SparseVector.Build("CHECKSUM Mismatch");
        var lower = SparseVector.Build("checksum mismatch");

        Assert.NotNull(upper);
        Assert.Equal(
            lower!.Indices.OrderBy(i => i),
            upper!.Indices.OrderBy(i => i));
    }

    [Fact]
    public void EmptyOrTermlessTextHasNoSparseVector()
    {
        Assert.Null(SparseVector.Build(null));
        Assert.Null(SparseVector.Build(""));
        Assert.Null(SparseVector.Build("   "));
        // Punctuation only: the tokenizer splits on non-alphanumerics and finds no terms.
        Assert.Null(SparseVector.Build("!!! ,. ---"));
    }
}
