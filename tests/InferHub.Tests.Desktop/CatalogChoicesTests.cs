using System.Text.Json.Nodes;
using InferHub.Desktop;

namespace InferHub.Tests;

/// <summary>Phase 119 in the app: the node's catalogue answer as rows — its order kept, its words chosen.</summary>
public sealed class CatalogChoicesTests
{
    private static JsonObject Reply() => JsonNode.Parse("""
        {
          "sort": "trending", "kind": "text", "note": "ordered by where the weights alone would sit",
          "sources": [{"name":"huggingface","ok":false,"count":0,"error":"Hugging Face did not answer: 429"},
                      {"name":"ollama","ok":true,"count":2}],
          "entries": [
            {"source":"ollama","name":"qwen2.5","link":"qwen2.5:32b","description":"Qwen2.5 models","capabilities":["tools"],
             "moe":false,"downloads":43700000,"updatedText":"2 years ago","strata":false,
             "fit":{"class":"card","weightsMiB":18502,"why":"32B parameters ≈ 18 GiB at 4-bit, on the card"},
             "sizes":[{"name":"7b","link":"qwen2.5:7b","fit":{"class":"card","weightsMiB":4048,"why":""}},
                      {"name":"32b","link":"qwen2.5:32b","fit":{"class":"card","weightsMiB":18502,"why":""}},
                      {"name":"72b","link":"qwen2.5:72b","fit":{"class":"card-and-ram","weightsMiB":41628,"why":""}}],
             "rank":0},
            {"source":"ollama","name":"glm-4.7-flash","link":"glm-4.7-flash","capabilities":[],"moe":false,"strata":false,
             "fit":{"class":"unknown","why":"the library shows no size"},"sizes":[],"rank":1}
          ]
        }
        """)!.AsObject();

    [Fact]
    public void RowsKeepTheNodesOrderAndShowTheClassAndWeights()
    {
        var rows = CatalogChoices.Rows(Reply());

        Assert.Equal(["qwen2.5", "glm-4.7-flash"], rows.Select(r => r.Name));
        Assert.Equal("qwen2.5  [fits on the card · 18 GiB of weights]", rows[0].Headline);
        Assert.Equal("qwen2.5:32b", rows[0].Link);
        Assert.Contains("44M pulls", rows[0].Details);
        Assert.Contains("updated 2 years ago", rows[0].Details);
        Assert.Equal("glm-4.7-flash  [size unknown]", rows[1].Headline);
    }

    [Fact]
    public void EachSizeIsLabelledWithItsOwnClass()
    {
        var sizes = CatalogChoices.Rows(Reply())[0].Sizes;

        Assert.Equal("72b — card + RAM, 41 GiB", sizes[2].Label);
        Assert.Equal("qwen2.5:7b", sizes[0].Link);
    }

    [Fact]
    public void ASourceThatDidNotAnswerIsSaidByName() =>
        Assert.Equal(["Hugging Face: Hugging Face did not answer: 429"], CatalogChoices.Problems(Reply()));

    [Theory]
    [InlineData(950, "950")]
    [InlineData(1_500, "1.5K")]
    [InlineData(149_888, "150K")]
    [InlineData(2_201_569, "2.2M")]
    public void CountsAreShortened(long n, string expected) => Assert.Equal(expected, CatalogChoices.Count(n));
}
