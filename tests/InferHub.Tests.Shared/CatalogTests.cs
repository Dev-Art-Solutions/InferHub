using System.Text.Json;
using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>Phase 119 D1: the catalogue's rough class — weights at 4-bit against the estimate's own usable VRAM and RAM.</summary>
public sealed class CatalogFitTests
{
    private static MachineFacts Machine(long vramMiB = 24576, long ramMiB = 65536, long? vramLimit = null) => CatalogMachine.Of(vramMiB, ramMiB, vramLimit);

    [Theory]
    [InlineData(7_000_000_000L, CatalogClasses.Card)]
    [InlineData(32_000_000_000L, CatalogClasses.Card)]
    [InlineData(72_000_000_000L, CatalogClasses.CardAndRam)]
    [InlineData(176_000_000_000L, CatalogClasses.TooBig)]
    public void TheClassFollowsTheWeightsOnA24GbCardWith64GbOfRam(long parameters, string expected)
    {
        var fit = CatalogRough.Classify(parameters, false, Machine());

        Assert.Equal(expected, fit.Class);
        Assert.Contains("4-bit", fit.Why);
    }

    [Fact]
    public void TheArithmeticIsShownAndTheContextIsLeftToCheckFit()
    {
        var fit = CatalogRough.Classify(7_000_000_000, false, Machine());

        Assert.Equal(4048, fit.WeightsMiB); // 7e9 × 4.85 bits / 8 / 2^20, rounded up
        Assert.Contains("7B parameters ≈ 4 GiB at 4-bit", fit.Why);
        Assert.Contains("the context is chosen in Check fit", fit.Why);
    }

    [Fact]
    public void AMixtureOfExpertsTooBigForTheCardSaysTheExpertsGoToRam()
    {
        var fit = CatalogRough.Classify(72_000_000_000, true, Machine());

        Assert.Equal(CatalogClasses.CardAndRam, fit.Class);
        Assert.Contains("experts in RAM", fit.Why);
    }

    [Fact]
    public void WithoutACardTheModelIsClassedForTheCpu()
    {
        Assert.Equal(CatalogClasses.Ram, CatalogRough.Classify(7_000_000_000, false, Machine(vramMiB: 0)).Class);
        Assert.Equal(CatalogClasses.TooBig, CatalogRough.Classify(200_000_000_000, false, Machine(vramMiB: 0)).Class);
    }

    [Fact]
    public void TheOwnersVramLimitIsTheCardTheClassIsFor()
    {
        // A 14B is 8 GiB at 4-bit: on a 24 GB card, but not under a 6 GB limit.
        Assert.Equal(CatalogClasses.Card, CatalogRough.Classify(14_000_000_000, false, Machine()).Class);
        Assert.Equal(CatalogClasses.CardAndRam, CatalogRough.Classify(14_000_000_000, false, Machine(vramLimit: 6144)).Class);
    }

    [Fact]
    public void NoParameterCountIsUnknownNotAGuess()
    {
        var fit = CatalogRough.Classify(null, false, Machine());

        Assert.Equal(CatalogClasses.Unknown, fit.Class);
        Assert.Null(fit.WeightsMiB);
    }

    [Fact]
    public void TheListIsOrderedByClassThenByEachSourcesOwnRank()
    {
        CatalogEntry Entry(string source, string name, string cls, int rank) =>
            new(source, name, name, null, [], null, false, null, null, null, null, null, false, [], new CatalogFit(cls, 1, ""), rank);

        var ordered = CatalogRough.Order(
        [
            Entry(CatalogSources.HuggingFace, "huge-trending", CatalogClasses.TooBig, 0),
            Entry(CatalogSources.HuggingFace, "split", CatalogClasses.CardAndRam, 1),
            Entry(CatalogSources.HuggingFace, "hf-card", CatalogClasses.Card, 2),
            Entry(CatalogSources.Ollama, "ollama-card", CatalogClasses.Card, 0),
            Entry(CatalogSources.Ollama, "nameless", CatalogClasses.Unknown, 1)
        ]);

        Assert.Equal(["ollama-card", "hf-card", "split", "nameless", "huge-trending"], ordered.Select(e => e.Name));
    }
}

/// <summary>Phase 119 D2: Ollama's library page, read by hand from a copy of the real one.</summary>
public sealed class OllamaLibraryPageTests
{
    [Fact]
    public void EachModelIsReadWithItsBadgesPullsAndDate()
    {
        var models = OllamaLibraryPage.Parse(OllamaLibraryFixture.Page);
        var qwen = models.Single(m => m.Name == "qwen2.5");

        Assert.Equal(6, models.Count);
        Assert.StartsWith("Qwen2.5 models are pretrained on Alibaba's latest", qwen.Description);
        Assert.Equal(["tools"], qwen.Capabilities);
        Assert.Equal(["0.5b", "1.5b", "3b", "7b", "14b", "32b", "72b"], qwen.Sizes);
        Assert.Equal(43_700_000, qwen.Pulls);
        Assert.Equal(new DateTimeOffset(2024, 9, 19, 16, 42, 0, TimeSpan.Zero), qwen.Updated);
        Assert.Equal("2 years ago", qwen.UpdatedText);
    }

    [Fact]
    public void ACloudOnlyModelAndAnEmbeddingModelAreToldApart()
    {
        var models = OllamaLibraryPage.Parse(OllamaLibraryFixture.Page);

        Assert.True(models.Single(m => m.Name == "minimax-m2.7").CloudOnly);
        Assert.False(models.Single(m => m.Name == "glm-4.7-flash").CloudOnly); // no size badge, but pullable
        Assert.Contains("embedding", models.Single(m => m.Name == "nomic-embed-text").Capabilities);
        Assert.Equal(["e2b", "e4b"], models.Single(m => m.Name == "gemma3n").Sizes);
    }

    [Theory]
    [InlineData("7b", 7_000_000_000L)]
    [InlineData("0.5b", 500_000_000L)]
    [InlineData("e2b", 2_000_000_000L)]
    [InlineData("8x7b", 56_000_000_000L)]
    [InlineData("335m", 335_000_000L)]
    [InlineData("latest", null)]
    public void ASizeBadgeIsAParameterCount(string badge, long? expected) =>
        Assert.Equal(expected, OllamaLibraryPage.Parameters(badge));

    [Fact]
    public void APageWithoutModelLinksReadsAsEmpty() =>
        Assert.Empty(OllamaLibraryPage.Parse("<html><body><h1>Something else</h1></body></html>"));

    [Fact]
    public void TheQueryHasNoTrendingOrderSoPopularIsUsed()
    {
        Assert.Equal("library?sort=popular", OllamaLibraryPage.Query(CatalogSorts.Trending, null));
        Assert.Equal("library?sort=newest&q=qwen%20coder", OllamaLibraryPage.Query(CatalogSorts.Newest, " qwen coder "));
    }
}

/// <summary>Phase 119 D3: Hugging Face's listing, as its API answers with <c>expand[]=gguf</c>.</summary>
public sealed class HuggingFaceListingTests
{
    // Trimmed from api/models?filter=gguf&sort=trendingScore&expand[]=gguf… on 2026-10-27.
    private const string Listing = """
        [{"_id":"6adb","id":"ISTA-DASLab/DeepSeek-V4.1-Flash-1017-GSQ-RCO-GGUF","lastModified":"2026-10-25T23:37:22.000Z","likes":371,"downloads":1105751,
          "gguf":{"total":196613849600,"architecture":"deepseek41-engram","totalFileSize":110595290816},
          "tags":["gguf","moe","deepseek","text-generation"],"pipeline_tag":"text-generation","createdAt":"2026-10-23T09:43:05.000Z"},
         {"_id":"6ad6","id":"unsloth/flint-GGUF","likes":278,"downloads":149888,
          "gguf":{"total":39785287456,"architecture":"flint","context_length":262144,"chat_template":"{{ x }}"},
          "tags":["gguf"],"pipeline_tag":"text-generation","createdAt":"2026-10-21T00:00:00.000Z"},
         {"_id":"6ad7","id":"someone/no-gguf-summary","tags":[]}]
        """;

    [Fact]
    public void TheParameterCountArchitectureContextAndMoeComeFromTheListing()
    {
        using var document = JsonDocument.Parse(Listing);
        var models = HuggingFaceListing.Parse(document.RootElement);

        Assert.Equal(3, models.Count);
        Assert.Equal(196_613_849_600, models[0].Parameters);
        Assert.True(models[0].Moe);
        Assert.Equal(1_105_751, models[0].Downloads);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 43, 5, TimeSpan.Zero), models[0].Created);
        Assert.Equal(262_144, models[1].TrainedContext);
        Assert.False(models[1].Moe);
        Assert.Null(models[2].Parameters);
    }

    [Fact]
    public void TheQueryAsksForGgufOfTheKindInTheChosenOrder()
    {
        var query = HuggingFaceListing.Query(CatalogSorts.Downloads, CatalogKinds.Vision, "qwen", 500);

        Assert.StartsWith("api/models?filter=gguf&pipeline_tag=image-text-to-text&sort=downloads&direction=-1&limit=100&search=qwen", query);
        Assert.Contains("expand[]=gguf", query);
        Assert.Contains("sort=trendingScore", HuggingFaceListing.Query(CatalogSorts.Trending, CatalogKinds.Text, null, 30));
        Assert.Contains("sort=createdAt", HuggingFaceListing.Query(CatalogSorts.Newest, CatalogKinds.Text, null, 30));
    }

    [Fact]
    public void ABodyThatIsNotAListIsAnError()
    {
        using var document = JsonDocument.Parse("""{"error":"rate limited"}""");
        Assert.Throws<InvalidDataException>(() => HuggingFaceListing.Parse(document.RootElement));
    }
}
