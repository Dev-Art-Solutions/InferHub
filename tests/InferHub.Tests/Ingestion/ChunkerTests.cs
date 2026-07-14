using InferHub.Coordinator.Ingestion;

namespace InferHub.Tests.Ingestion;

public class ChunkerTests
{
    [Fact]
    public void EmptyDocumentYieldsNoChunks()
    {
        var chunker = new Chunker(maxChars: 200, overlapChars: 20);

        Assert.Empty(chunker.ChunkText(""));
        Assert.Empty(chunker.ChunkText("   \n\n  \t "));
    }

    [Fact]
    public void DocumentSmallerThanOneChunkYieldsExactlyOneChunk()
    {
        var chunker = new Chunker(maxChars: 200, overlapChars: 20);

        var chunks = chunker.ChunkText("A short paragraph that easily fits.");

        var chunk = Assert.Single(chunks);
        Assert.Equal("A short paragraph that easily fits.", chunk);
    }

    [Fact]
    public void NoChunkExceedsMaxChars()
    {
        var chunker = new Chunker(maxChars: 120, overlapChars: 30);
        var text = string.Join("\n\n", Enumerable.Range(0, 40).Select(i => $"Paragraph {i} with some filler words to give it length."));

        var chunks = chunker.ChunkText(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 120, $"chunk of {c.Length} chars exceeded the 120-char maximum"));
    }

    [Fact]
    public void ConsecutiveChunksOverlap()
    {
        // Paragraphs are short enough that several fit per chunk, so the atom-aligned overlap has
        // something to carry: chunk N+1 opens with a run of chunk N's trailing paragraphs — one or
        // more, whatever fits the overlap budget — and always includes the last of them.
        var chunker = new Chunker(maxChars: 100, overlapChars: 40);
        var text = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Para {i} filler."));

        var chunks = chunker.ChunkText(text);

        Assert.True(chunks.Count > 1);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var previous = chunks[i].Split("\n\n");
            var next = chunks[i + 1].Split("\n\n");

            Assert.Contains(previous[^1], next);
            Assert.Contains(next[0], previous);
        }
    }

    [Fact]
    public void EveryParagraphSurvivesSomewhere()
    {
        var chunker = new Chunker(maxChars: 100, overlapChars: 20);
        var paragraphs = Enumerable.Range(0, 25).Select(i => $"Paragraph number {i} here.").ToArray();

        var chunks = chunker.ChunkText(string.Join("\n\n", paragraphs));

        var joined = string.Join("\n", chunks);
        Assert.All(paragraphs, p => Assert.Contains(p, joined, StringComparison.Ordinal));
    }

    [Fact]
    public void CodeFenceIsNotSplitWhenItFits()
    {
        var chunker = new Chunker(maxChars: 400, overlapChars: 0);
        var text =
            "Intro paragraph.\n\n" +
            "```csharp\n" +
            "var x = 1;\n" +
            "\n" +                       // a blank line inside the fence must not end the atom
            "var y = 2;\n" +
            "```\n\n" +
            "Trailing paragraph.";

        var chunks = chunker.ChunkText(text);

        var fenced = Assert.Single(chunks, c => c.Contains("```csharp", StringComparison.Ordinal));
        Assert.Contains("var x = 1;", fenced, StringComparison.Ordinal);
        Assert.Contains("var y = 2;", fenced, StringComparison.Ordinal);
        Assert.Contains("```", fenced[3..], StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownTableStaysWithItsHeaderRow()
    {
        var chunker = new Chunker(maxChars: 400, overlapChars: 0);
        var text =
            "Before.\n\n" +
            "| Name | Role |\n" +
            "|------|------|\n" +
            "| Ada  | Eng  |\n" +
            "| Bob  | Ops  |\n\n" +
            "After.";

        var chunks = chunker.ChunkText(text);

        var table = Assert.Single(chunks, c => c.Contains("| Ada", StringComparison.Ordinal));
        Assert.Contains("| Name | Role |", table, StringComparison.Ordinal);
    }

    [Fact]
    public void PathologicalSingleLineIsHardSplit()
    {
        var chunker = new Chunker(maxChars: 1000, overlapChars: 100);
        var line = new string('x', 500_000);

        var chunks = chunker.ChunkText(line);

        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
        Assert.Equal(500_000, chunks.Sum(c => c.Length));
    }

    [Fact]
    public void LongParagraphFallsBackToSentenceBoundaries()
    {
        var chunker = new Chunker(maxChars: 120, overlapChars: 0);
        var text = string.Join(" ", Enumerable.Range(0, 20).Select(i => $"Sentence number {i} is here."));

        var chunks = chunker.ChunkText(text);

        Assert.True(chunks.Count > 1);
        // A sentence-boundary split never leaves a chunk starting mid-word.
        Assert.All(chunks, c => Assert.StartsWith("Sentence", c, StringComparison.Ordinal));
    }

    [Fact]
    public void ChunkIndicesRunContinuouslyAcrossPagesAndKeepThePage()
    {
        var chunker = new Chunker(maxChars: 60, overlapChars: 0);
        var document = new ExtractedDocument("application/pdf",
        [
            new ExtractedPage(1, string.Join("\n\n", Enumerable.Range(0, 4).Select(i => $"One {i} filler text."))),
            new ExtractedPage(2, string.Join("\n\n", Enumerable.Range(0, 4).Select(i => $"Two {i} filler text.")))
        ]);

        var chunks = chunker.Chunk(document);

        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        Assert.Contains(chunks, c => c.Page == 1);
        Assert.Contains(chunks, c => c.Page == 2);
        Assert.All(chunks.Where(c => c.Page == 1), c => Assert.Contains("One", c.Text, StringComparison.Ordinal));
        Assert.All(chunks.Where(c => c.Page == 2), c => Assert.Contains("Two", c.Text, StringComparison.Ordinal));
    }
}
