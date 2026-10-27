using System.Net;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Fit;
using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 119 on the node: both sources asked over HTTP as theirs answer, the list classed against this machine and
/// ordered by it, each source refused or failing on its own (D4), and the answers kept ten minutes.
/// </summary>
public sealed class CatalogServiceTests
{
    private const string Listing = """
        [{"id":"big/DeepSeek-GGUF","downloads":1000000,"gguf":{"total":114925568,"architecture":"deepseek2"},"tags":["gguf","moe"],"pipeline_tag":"text-generation"},
         {"id":"mid/Seventy-GGUF","downloads":500000,"gguf":{"total":70000000000,"architecture":"llama"},"tags":["gguf"],"pipeline_tag":"text-generation"},
         {"id":"small/Eight-GGUF","downloads":100,"gguf":{"total":8000000000,"architecture":"llama","context_length":131072},"tags":["gguf"],"pipeline_tag":"text-generation"},
         {"id":"ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF","downloads":90,"gguf":{"total":35000000000,"architecture":"qwen35moe"},"tags":["gguf"],"pipeline_tag":"text-generation"}]
        """;

    private readonly Dictionary<string, int> asked = new();
    private Func<Uri, HttpResponseMessage> answer;

    // Each repo's tree: its quants' real bytes. The DeepSeek one is the trap found live — the listing's parameter count
    // (115M) is its LoRA's, the model is 200 GB.
    private static readonly Dictionary<string, (string Path, long Size)[]> Trees = new()
    {
        ["big/DeepSeek-GGUF"] = [("IQ1_M/DeepSeek-IQ1_M-00001-of-00002.gguf", 110_000_000_000), ("IQ1_M/DeepSeek-IQ1_M-00002-of-00002.gguf", 90_000_000_000), ("lora/DeepSeek-LoRA.gguf", 460_000_000), ("imatrix_unsloth.gguf", 2_500_000_000), ("mtp-DeepSeek-Q8_0.gguf", 4_140_000_000), ("DeepSeek-MTP-draft.gguf", 4_140_000_000)],
        ["mid/Seventy-GGUF"] = [("Seventy-Q4_K_M.gguf", 42_500_000_000), ("Seventy-Q2_K.gguf", 20_000_000_000)],
        ["small/Eight-GGUF"] = [("Eight-Q4_K_M.gguf", 4_900_000_000), ("Eight-Q8_0.gguf", 8_500_000_000), ("Eight-F16.gguf", 16_000_000_000), ("mmproj-Eight-f16.gguf", 600_000_000)]
    };

    public CatalogServiceTests()
    {
        answer = uri => uri.Host == "hub.test"
            ? Hub(uri)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OllamaLibraryFixture.Page) };
    }

    private static HttpResponseMessage Hub(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        if (!path.Contains("/tree/", StringComparison.Ordinal))
        {
            return Json(Listing);
        }

        var repo = path["/api/models/".Length..path.IndexOf("/tree/", StringComparison.Ordinal)];
        var files = Trees.TryGetValue(repo, out var tree) ? tree : [];
        return Json(System.Text.Json.JsonSerializer.Serialize(files.Select(f => new { type = "file", path = f.Path, size = f.Size })));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private CatalogService Service(bool huggingFace = true, bool ollama = true, TimeProvider? time = null)
    {
        var http = new HttpClient(new Handler(request =>
        {
            var key = request.RequestUri!.Host;
            asked[key] = asked.GetValueOrDefault(key) + 1;
            return answer(request.RequestUri);
        }));

        return new CatalogService(
            huggingFace ? () => new HuggingFaceClient(http, new Uri("https://hub.test/"), null) : null,
            http,
            new Uri("https://library.test/"),
            () => CatalogMachine.Of(ollama: ollama),
            time);
    }

    private static CatalogService.Request All(string? source = null, bool fitsOnly = false, string? kind = null) => new(source, null, kind, null, null, fitsOnly);

    [Fact]
    public async Task BothSourcesAreListedFittingFirstAndTooBigLast()
    {
        var list = await Service().ListAsync(All(), CancellationToken.None);

        Assert.All(list.Sources, s => Assert.True(s.Ok, s.Error));
        Assert.Equal(CatalogClasses.Card, list.Entries[0].Fit.Class);
        Assert.Equal("big/DeepSeek-GGUF", list.Entries[^1].Name); // first on the Hub, last here — 200 GB, not its LoRA's 115M
        Assert.Equal(CatalogClasses.TooBig, list.Entries[^1].Fit.Class);
        Assert.Contains("weights alone", list.Note);
        Assert.Contains("no trending order", list.Note);
    }

    [Fact]
    public async Task AnOllamaModelOffersItsLargestSizeThatFitsAndEverySizeClassed()
    {
        var list = await Service().ListAsync(All(CatalogSources.Ollama), CancellationToken.None);
        var qwen = list.Entries.Single(e => e.Name == "qwen2.5");

        Assert.Equal("qwen2.5:32b", qwen.Link); // 32B ≈ 18 GiB on a 24 GB card; 72B is a split
        Assert.Equal(CatalogClasses.Card, qwen.Fit.Class);
        Assert.Equal(CatalogClasses.CardAndRam, qwen.Sizes.Single(s => s.Name == "72b").Fit.Class);
        Assert.Equal(43_700_000, qwen.Downloads);
        Assert.DoesNotContain(list.Entries, e => e.Name == "minimax-m2.7");     // cloud only: nothing to pull
        Assert.DoesNotContain(list.Entries, e => e.Name == "nomic-embed-text"); // not a text model to chat with
        Assert.Equal(CatalogClasses.Unknown, list.Entries.Single(e => e.Name == "glm-4.7-flash").Fit.Class);
        Assert.True(list.Entries.Single(e => e.Name == "mixtral").Moe);
    }

    [Fact]
    public async Task AStrataRepoIsLeftForStrataToSize()
    {
        var list = await Service().ListAsync(All(CatalogSources.HuggingFace), CancellationToken.None);
        var strata = list.Entries.Single(e => e.Name.StartsWith("ISTA-DASLab/", StringComparison.Ordinal));

        Assert.True(strata.Strata);
        Assert.Equal(CatalogClasses.Unknown, strata.Fit.Class);
        Assert.Contains("Strata", strata.Fit.Why);
    }

    [Fact]
    public async Task VisionKeepsOnlyOllamasVisionModels()
    {
        var list = await Service().ListAsync(All(CatalogSources.Ollama, kind: CatalogKinds.Vision), CancellationToken.None);

        Assert.Equal(["gemma3n"], list.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task FitsOnlyDropsWhatThisMachineCannotHold()
    {
        var list = await Service().ListAsync(All(fitsOnly: true), CancellationToken.None);

        Assert.NotEmpty(list.Entries);
        Assert.All(list.Entries, e => Assert.True(e.Fit.Fits));
    }

    [Fact]
    public async Task EachSourceIsAskedOnlyWithConsentAndTheOtherStillAnswers()
    {
        var list = await Service(huggingFace: false, ollama: false).ListAsync(All(), CancellationToken.None);

        Assert.Empty(list.Entries);
        Assert.Contains("HuggingFace:Enabled=false", list.Sources.Single(s => s.Name == CatalogSources.HuggingFace).Error);
        Assert.Contains("no Ollama engine", list.Sources.Single(s => s.Name == CatalogSources.Ollama).Error);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ASourceThatFailsSaysSoAndTheOtherIsStillListed()
    {
        answer = uri => uri.Host == "hub.test"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OllamaLibraryFixture.Page) };

        var list = await Service().ListAsync(All(), CancellationToken.None);
        var hf = list.Sources.Single(s => s.Name == CatalogSources.HuggingFace);

        Assert.False(hf.Ok);
        Assert.Contains("429", hf.Error);
        Assert.All(list.Entries, e => Assert.Equal(CatalogSources.Ollama, e.Source));
        Assert.NotEmpty(list.Entries);
    }

    [Fact]
    public async Task ALibraryPageThatChangedShapeIsAnErrorNotAnEmptyList()
    {
        answer = uri => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body>a redesign</body></html>") };

        var list = await Service().ListAsync(All(CatalogSources.Ollama), CancellationToken.None);
        var ollama = list.Sources.Single();

        Assert.False(ollama.Ok);
        Assert.Contains("changed shape", ollama.Error);
    }

    [Fact]
    public async Task AnswersAreKeptTenMinutesPerQuery()
    {
        var time = new ShiftableTime();
        var service = Service(time: time);

        await service.ListAsync(All(), CancellationToken.None);
        await service.ListAsync(All(fitsOnly: true), CancellationToken.None); // a filter of the same answer
        Assert.Equal(4, asked["hub.test"]); // the listing and three trees (the Strata repo is Strata's to size)
        Assert.Equal(1, asked["library.test"]);

        time.Advance(TimeSpan.FromMinutes(11));
        await service.ListAsync(All(), CancellationToken.None);
        Assert.Equal(8, asked["hub.test"]);
    }

    [Fact]
    public async Task AHuggingFaceRepoIsClassedByItsQuantsRealBytesAndOpensAtAFittingFourBit()
    {
        var list = await Service().ListAsync(All(CatalogSources.HuggingFace), CancellationToken.None);
        var eight = list.Entries.Single(e => e.Name == "small/Eight-GGUF");
        var seventy = list.Entries.Single(e => e.Name == "mid/Seventy-GGUF");
        var deepseek = list.Entries.Single(e => e.Name == "big/DeepSeek-GGUF");

        Assert.Equal(["Q4_K_M", "Q8_0", "F16"], eight.Sizes.Select(s => s.Name));
        Assert.Equal("small/Eight-GGUF:Q4_K_M", eight.Link); // the 4-bit, though F16 fits the card too
        Assert.Equal(CatalogClasses.Card, seventy.Fit.Class); // its Q2_K is on the card; the Q4_K_M would be a split
        Assert.Equal("mid/Seventy-GGUF:Q2_K", seventy.Link);
        Assert.Equal(CatalogClasses.TooBig, deepseek.Fit.Class);
        Assert.Equal(["IQ1_M"], deepseek.Sizes.Select(s => s.Name)); // no LoRA, imatrix or MTP draft is a quant of the model
    }

    [Theory]
    [InlineData("lora/DeepSeek-LoRA.gguf", true)]
    [InlineData("imatrix_unsloth.gguf", true)]
    [InlineData("mtp-Qwen3.8-Flash-Next-Q8_0.gguf", true)]
    [InlineData("Qwen3.8-Flash-Next-Uncensored-MTP-draft.gguf", true)]
    [InlineData("Qwen3.8-Flash-Next-Uncensored-IQ4_XS-MTP-00001-of-00003.gguf", false)]
    [InlineData("IQ2_XS/Qwen3.8-Flash-Next-IQ2_XS-00001-of-00002.gguf", false)]
    public void SideFilesAreNotQuants(string path, bool side) => Assert.Equal(side, CatalogService.IsNotAModel(path));

    [Fact]
    public async Task ARepoWhoseFilesCannotBeReadFallsBackToTheSummaryAndSaysSo()
    {
        answer = uri => uri.AbsolutePath.Contains("/tree/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Hub(uri);

        var list = await Service().ListAsync(All(CatalogSources.HuggingFace), CancellationToken.None);
        var eight = list.Entries.Single(e => e.Name == "small/Eight-GGUF");

        Assert.Equal(CatalogClasses.Card, eight.Fit.Class);
        Assert.Contains("can describe a side file", eight.Fit.Why);
    }

    [Theory]
    [InlineData("everywhere", null, null)]
    [InlineData(null, "best", null)]
    [InlineData(null, null, "audio")]
    public async Task AnUnknownSourceSortOrKindIsA400(string? source, string? sort, string? kind)
    {
        var ex = await Assert.ThrowsAsync<FitRefusedException>(() => Service().ListAsync(new CatalogService.Request(source, sort, kind, null, null, false), CancellationToken.None));
        Assert.Equal(400, ex.Status);
    }
}
