using System.Text.Json;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Vector;

public class InvertedIndexTests
{
    [Fact]
    public void RanksDocumentWithTheRareTermHighest()
    {
        var index = new InvertedIndex();
        index.Index("prose", "General information about the payment subsystem and its features and error handling.");
        index.Index("code", "Error E-4021 indicates a checksum mismatch on the uploaded batch.");
        index.Index("other", "The weather in Sofia is pleasant in spring.");

        var hits = index.Search("what does error E-4021 mean", 3);

        Assert.NotEmpty(hits);
        // "4021" is a hapax — only the code chunk carries it, so idf pulls it to the top.
        Assert.Equal("code", hits[0].Id);
    }

    [Fact]
    public void RemoveEliminatesDocumentFromResults()
    {
        var index = new InvertedIndex();
        index.Index("a", "checksum mismatch error");
        index.Index("b", "unrelated content");

        Assert.True(index.Remove("a"));
        var hits = index.Search("checksum", 5);

        Assert.DoesNotContain(hits, h => h.Id == "a");
        Assert.Equal(1, index.DocumentCount);
    }

    [Fact]
    public void ReindexReplacesOldTextRatherThanAccumulating()
    {
        var index = new InvertedIndex();
        index.Index("a", "alpha bravo charlie");
        index.Index("a", "delta echo foxtrot"); // same id, new text

        Assert.Empty(index.Search("alpha", 5));            // the old term is gone
        Assert.Equal("a", index.Search("delta", 5)[0].Id); // the new term is present
        Assert.Equal(1, index.DocumentCount);              // still exactly one document
    }

    [Fact]
    public void EmptyTextRemovesTheDocument()
    {
        var index = new InvertedIndex();
        index.Index("a", "some content");
        index.Index("a", "");

        Assert.Equal(0, index.DocumentCount);
        Assert.Empty(index.Search("content", 5));
    }

    [Fact]
    public void SearchOnEmptyIndexReturnsEmpty()
    {
        var index = new InvertedIndex();
        Assert.Empty(index.Search("anything", 5));
    }

    [Fact]
    public void TokenizeSplitsOnNonAlphanumericAndLowercases()
    {
        var tokens = InvertedIndex.Tokenize("Error E-4021: checksum_mismatch!").ToArray();
        Assert.Equal(["error", "e", "4021", "checksum", "mismatch"], tokens);
    }

    [Fact]
    public async Task LocalVectorStoreRebuildsKeywordIndexFromRawAfterRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferhub-kw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new VectorStoreOptions { Enabled = true, DataDirectory = dir, Distance = "cosine" });

        using (var store = new LocalVectorStore(options, NullLogger<LocalVectorStore>.Instance))
        {
            await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
            await store.UpsertAsync("docs", new VectorUpsert("code", [0f, 1f],
                Payload: JsonSerializer.SerializeToElement(new { text = "Error E-4021 checksum mismatch." })));
        }

        // Fresh instance over the same directory: the keyword index must come back derived from the
        // raw store, exactly like the vector FlatIndex does — no separate on-disk keyword copy.
        using (var reopened = new LocalVectorStore(options, NullLogger<LocalVectorStore>.Instance))
        {
            var hits = await reopened.SearchKeywordAsync("docs", "E-4021", 5);
            Assert.Equal("code", Assert.Single(hits).Id);
        }
    }

    [Fact]
    public async Task LocalVectorStoreKeywordSearchDropsDeletedChunks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferhub-kw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var options = Options.Create(new VectorStoreOptions { Enabled = true, DataDirectory = dir, Distance = "cosine" });

        using var store = new LocalVectorStore(options, NullLogger<LocalVectorStore>.Instance);
        await store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        await store.UpsertAsync("docs", new VectorUpsert("a", [1f, 0f], Payload: JsonSerializer.SerializeToElement(new { text = "checksum mismatch" })));
        await store.DeleteAsync("docs", "a");

        var hits = await store.SearchKeywordAsync("docs", "checksum", 5);
        Assert.Empty(hits);
    }
}
