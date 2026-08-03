using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 38. RAG on a standalone node: what it serves, what it refuses, and — first — what it
/// refuses to boot with.
/// </summary>
public class SoloRetrievalTests
{
    // ---- D1: it only exists where there is no coordinator -------------------------------------

    [Fact]
    public async Task RetrievalTogetherWithACoordinatorIsAStartupFailureNamingBothKeys()
    {
        // The most important line in the phase. A meshed node already holds replicas derived from
        // its hub; a second, authoritative store in the same process is two sources of truth for
        // one collection name (rule 4). It refuses rather than quietly disabling, because silently
        // switched-off grounding is confident, fluent, ungrounded answers with no signal at all.
        var failure = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=true",
            "--Coordinator:Enabled=true",
            "--Coordinator:Url=http://localhost:5080/",
            "--LocalApi:Retrieval:Enabled=true");

        Assert.Contains("LocalApi:Retrieval:Enabled", failure.Message);
        Assert.Contains("Coordinator:Enabled", failure.Message);
    }

    [Fact]
    public async Task RetrievalWithoutTheLocalApiIsAlsoAStartupFailure()
    {
        // Nothing would ever reach it: retrieval is served over the local API.
        var failure = await SoloHost.StartExpectingFailureAsync(
            "--LocalApi:Enabled=false",
            "--Coordinator:Enabled=false",
            "--LocalApi:Retrieval:Enabled=true");

        Assert.Contains("LocalApi:Enabled", failure.Message);
    }

    [Fact]
    public void TheValidatorIsSilentWhenTheFeatureIsOff()
    {
        // A node that leaves retrieval alone must boot past a half-edited section, exactly as the
        // supervisor's and the local API's validators do.
        var result = Validate(new LocalRetrievalOptions { Enabled = false, Distance = "nonsense" });

        Assert.False(result.Failed);
    }

    [Fact]
    public void BadValuesAreRejectedWithTheHubsOwnWording()
    {
        var result = Validate(
            new LocalRetrievalOptions
            {
                Enabled = true,
                Distance = "manhattan",
                Retrieval = { Mode = "fuzzy", Template = "no placeholder here" }
            },
            meshed: false);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Distance"));
        Assert.Contains(result.Failures!, f => f.Contains("Mode"));
        Assert.Contains(result.Failures!, f => f.Contains("{context}"));
    }

    // ---- D7 / phase-44 D3: with retrieval off, the routes answer 501 -------------------------

    /// <summary>
    /// <b>Amended in phase 44 (D3).</b> These were 404s until v3.11, because the routes were mapped
    /// only when a corpus was composed at startup. From v3.12 a coordinator can start a corpus on a
    /// running node, and ASP.NET cannot map an endpoint after the application has started — so they
    /// are mapped unconditionally and answer the retrieval refusal instead.
    /// </summary>
    /// <remarks>
    /// The 501 is the better answer regardless of the mechanics, and it is the one this repo already
    /// chose twice: tools (phase 41) and audio (phase 42) both map their routes with the feature off,
    /// because "this host could serve that if configured" is a different fact from "wrong URL".
    /// </remarks>
    [Theory]
    [InlineData("/api/collections")]
    [InlineData("/api/collections/docs/documents")]
    [InlineData("/api/vector/docs/anything")]
    public async Task WithRetrievalOffTheRagRoutesAnswer501RatherThan404(string path)
    {
        await using var host = await SoloHost.StartAsync();

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains("retrieval is not available on this node", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithRetrievalOffAHeaderIsStillA501()
    {
        await using var host = await SoloHost.StartAsync();

        var response = await host.Client.SendAsync(Chat("docs"));

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task WithRetrievalOffNoCorpusDirectoryIsCreated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "inferhub-rag-" + Guid.NewGuid().ToString("N"));

        await using var host = await SoloHost.StartAsync(null, $"--LocalApi:Retrieval:DataDirectory={directory}");

        // A node that upgrades and changes no config must not acquire a document store.
        Assert.False(Directory.Exists(directory));
    }

    // ---- the point of the phase: ingest, search, ground --------------------------------------

    [Fact]
    public async Task IngestThenSearchThenAGroundedAnswer()
    {
        await using var host = await StartWithRetrievalAsync();

        await IngestAsync(host, "handbook", "leave-policy", "Employees accrue 25 days of annual leave each year.");
        await IngestAsync(host, "handbook", "kitchen", "The espresso machine is descaled every second Friday.");

        // 1. It retrieves the right chunk, and says so where an operator can see it.
        var search = await host.Client.PostAsync(
            "/api/collections/handbook/search",
            Json("""{"query":"how much annual leave do I get?"}"""));
        var hits = (await search.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hits");

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal("leave-policy", hits[0].GetProperty("documentId").GetString());

        // 2. The same corpus grounds a chat answer, over the same header the hub takes.
        var chat = await host.Client.SendAsync(Chat("handbook", "how much annual leave do I get?"));

        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);

        // The context actually reached the backend — not merely "the request succeeded".
        Assert.Contains("25 days of annual leave", host.Backend.LastRequestJson);

        // And the caller can cite it.
        var sources = chat.Headers.GetValues("X-InferHub-Sources").Single();
        Assert.Contains("leave-policy", sources);
    }

    [Fact]
    public async Task TheGroundingSurvivesARestartBecauseTheCorpusIsOnDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "inferhub-rag-" + Guid.NewGuid().ToString("N"));

        try
        {
            await using (var first = await StartWithRetrievalAsync($"--LocalApi:Retrieval:DataDirectory={directory}"))
            {
                await IngestAsync(first, "handbook", "leave-policy", "Employees accrue 25 days of annual leave each year.");
            }

            await using var second = await StartWithRetrievalAsync($"--LocalApi:Retrieval:DataDirectory={directory}");

            var search = await second.Client.PostAsync(
                "/api/collections/handbook/search",
                Json("""{"query":"annual leave"}"""));
            var hits = (await search.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hits");

            Assert.Equal("leave-policy", hits[0].GetProperty("documentId").GetString());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheCollectionIsProvisionedByTheFirstIngestWithAMeasuredDimension()
    {
        await using var host = await StartWithRetrievalAsync();

        await IngestAsync(host, "fresh", "one", "some text");

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/api/collections/fresh");

        // Measured from the vectors that came back, never guessed (phase-31 D5).
        Assert.Equal(TestEmbeddings.Dimension, body.GetProperty("dimension").GetInt32());
    }

    [Fact]
    public async Task ReIngestingTheSameBytesIsANoOpAndSaysSo()
    {
        await using var host = await StartWithRetrievalAsync();

        await IngestAsync(host, "handbook", "doc", "the same words every time");
        var second = await IngestAsync(host, "handbook", "doc", "the same words every time");

        Assert.Equal("unchanged", second.GetProperty("status").GetString());
    }

    // ---- D5: no PDF, loudly -------------------------------------------------------------------

    [Fact]
    public async Task APdfUploadIs415ThatNamesTheLimitation()
    {
        await using var host = await StartWithRetrievalAsync();

        var response = await host.Client.PostAsync(
            "/api/collections/handbook/documents",
            Json("""{"id":"manual","text":"%PDF-1.4 whatever","contentType":"application/pdf"}"""));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        // It must name what to do next. "PDF extraction is not available in this build" tells an
        // operator nothing, and a silently bad extraction would be worse than either (phase-23 D4).
        Assert.Contains("coordinator", body.GetProperty("error").GetString());
    }

    // ---- honest failures ----------------------------------------------------------------------

    [Fact]
    public async Task AMissingCollectionIs424ByDefaultRatherThanAQuietlyUngroundedAnswer()
    {
        await using var host = await StartWithRetrievalAsync();

        var response = await host.Client.SendAsync(Chat("no-such-collection"));

        // Retrieval:OnMissing defaults to 'error', so the caller is told rather than answered.
        Assert.Equal(HttpStatusCode.FailedDependency, response.StatusCode);

        // The query was embedded — that happens before the collection is consulted — but no chat
        // ever reached the backend, which is the part that matters: nothing was answered.
        Assert.DoesNotContain("messages", host.Backend.LastRequestJson ?? string.Empty);
    }

    [Fact]
    public async Task PassthroughAnswersUngroundedButOnlyWhenAskedTo()
    {
        await using var host = await StartWithRetrievalAsync("--LocalApi:Retrieval:Retrieval:OnMissing=passthrough");

        var response = await host.Client.SendAsync(Chat("no-such-collection"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Answered, and honest about it: no citation header on an answer nothing grounded.
        Assert.False(response.Headers.Contains("X-InferHub-Sources"));
    }

    [Fact]
    public async Task AnEmbeddingModelThisBoxDoesNotServeFailsTheIngestAndNamesIt()
    {
        // The hub's NoEmbeddingNodeException keeps its meaning on one machine: nobody *here*
        // serves it. The shared pipeline treats it as an unrecoverable batch failure — it is not
        // retried, because "no such model" will not fix itself in 400 ms — so the document is
        // reported partial with the model named, exactly as on a hub whose fleet lost the model.
        await using var host = await StartWithRetrievalAsync("--LocalApi:Retrieval:DefaultEmbeddingModel=not-installed");

        var response = await host.Client.PostAsync(
            "/api/collections/handbook/documents",
            Json("""{"id":"doc","text":"hello"}"""));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("partial", body.GetProperty("status").GetString());
        Assert.Contains("not-installed", body.GetProperty("error").GetString());

        // And nothing was left behind for the next caller to misread as provisioned: the dimension
        // is measured from the first successful batch, so a batch that never succeeds creates no
        // collection (phase-31 D5).
        var collection = await host.Client.GetAsync("/api/collections/handbook");
        Assert.Equal(HttpStatusCode.NotFound, collection.StatusCode);
    }

    [Fact]
    public async Task AnUnservableEmbeddingModelMakesGroundingA424RatherThanAnUngroundedAnswer()
    {
        await using var host = await StartWithRetrievalAsync();
        await IngestAsync(host, "handbook", "doc", "Employees accrue 25 days of annual leave.");

        var request = Chat("handbook");
        request.Headers.Add("X-InferHub-Retrieve-Model", "not-installed");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.FailedDependency, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownRetrievalModeIsA400NotASilentFallback()
    {
        await using var host = await StartWithRetrievalAsync();

        var request = Chat("handbook");
        request.Headers.Add("X-InferHub-Retrieve-Mode", "magic");

        var response = await host.Client.SendAsync(request);

        // A caller who asked for hybrid and quietly got vector would draw the wrong conclusion
        // from the results (phase-24 D5). Same rule, same status, on both hosts.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task APartialDocumentIsRetriedRatherThanReportedUnchanged()
    {
        // The content-hash short circuit must not fire on a document that is half-missing: "you
        // already have this" would be a lie (phase 23). With an embedding model this box cannot
        // serve, every attempt fails the same way — and the second one failing is the proof that
        // it was retried rather than skipped.
        await using var host = await StartWithRetrievalAsync("--LocalApi:Retrieval:DefaultEmbeddingModel=not-installed");

        var first = await host.Client.PostAsync(
            "/api/collections/handbook/documents",
            Json("""{"id":"doc","text":"hello"}"""));
        var second = await host.Client.PostAsync(
            "/api/collections/handbook/documents",
            Json("""{"id":"doc","text":"hello"}"""));

        Assert.Equal("partial", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal("partial", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    // ---- D7: reranking is an improvement, never a dependency ---------------------------------

    [Fact]
    public async Task ARerankerThatAnswersWithProseLeavesTheRankingAlone()
    {
        // Phase-24 D4, and it matters more on one machine than on a fleet: a solo box is one
        // wedged backend away from every rerank failing. The model here answers with prose rather
        // than a score array, which is the most common real failure.
        await using var host = await StartWithRetrievalAsync();

        await IngestAsync(host, "handbook", "leave-policy", "Employees accrue 25 days of annual leave each year.");
        await IngestAsync(host, "handbook", "kitchen", "The espresso machine is descaled every second Friday.");
        await IngestAsync(host, "handbook", "expenses", "Expense claims over 200 EUR need a receipt.");

        host.Backend.BlockingResponse = """
        {"model":"llama3","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"Sure! Passage one looks most relevant."},"done":true,"done_reason":"stop"}
        """;

        var plain = await RankAsync(host, rerank: false);
        var reranked = await RankAsync(host, rerank: true);

        Assert.Equal(plain, reranked);
        Assert.NotEmpty(plain);

        // And the reranker is genuinely wired — otherwise the assertion above would pass because
        // nothing ran, which is the quietest way for a test like this to be worthless.
        host.Backend.BlockingResponse = """
        {"model":"llama3","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"[1, 2, 3]"},"done":true,"done_reason":"stop"}
        """;

        Assert.Equal(plain.Reverse(), await RankAsync(host, rerank: true));
    }

    /// <summary>The rerank model is named explicitly: with none resolved the reranker skips, which
    /// is correct (phase-24 D4) and would make the "it is genuinely wired" assertion vacuous.</summary>
    private static async Task<string[]> RankAsync(SoloHost host, bool rerank)
    {
        var response = await host.Client.PostAsync(
            "/api/collections/handbook/search",
            Json(JsonSerializer.Serialize(new { query = "how much annual leave?", rerank, model = "llama3" })));

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return [.. body.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("id").GetString() ?? "")];
    }

    // ---- D9: status reports the corpus and still does not fake a fleet ------------------------

    [Fact]
    public async Task StatusReportsTheCorpusWithoutInventingFleetNumbers()
    {
        await using var host = await StartWithRetrievalAsync();
        await IngestAsync(host, "handbook", "doc", "hello world");

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");
        var retrieval = body.GetProperty("retrieval");

        Assert.True(retrieval.GetProperty("enabled").GetBoolean());
        Assert.Equal("handbook", retrieval.GetProperty("collections")[0].GetProperty("name").GetString());

        // No replica counts, no under-replication gauge, no queue: a node with no coordinator has
        // no concept of any of them (phase-37 D5).
        Assert.False(retrieval.TryGetProperty("replicas", out _));
        Assert.False(body.TryGetProperty("nodes", out _));
    }

    [Fact]
    public async Task StatusSaysRetrievalIsOffWhenItIsOff()
    {
        await using var host = await SoloHost.StartAsync();

        var body = await host.Client.GetFromJsonAsync<JsonElement>("/api/status");

        // The answer that explains a 501 to whoever is reading the status page.
        Assert.False(body.GetProperty("retrieval").GetProperty("enabled").GetBoolean());
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>A solo node with a corpus, a scripted backend that embeds deterministically.</summary>
    internal static async Task<SoloHost> StartWithRetrievalAsync(params string[] settings)
    {
        var backend = new ScriptedBackend
        {
            DeterministicEmbeddings = true,
            Models = [new ModelInfo("llama3", "sha256:abc", 1), new ModelInfo("test-embed", null, null)]
        };

        return await SoloHost.StartAsync(
            backend,
            [
                "--LocalApi:Retrieval:Enabled=true",
                "--LocalApi:Retrieval:DefaultEmbeddingModel=test-embed",
                .. settings
            ]);
    }

    internal static async Task<JsonElement> IngestAsync(SoloHost host, string collection, string id, string text)
    {
        var response = await host.Client.PostAsync(
            $"/api/collections/{collection}/documents",
            Json(JsonSerializer.Serialize(new { id, text })));

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    internal static HttpRequestMessage Chat(string collection, string question = "hi")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = Json(JsonSerializer.Serialize(new
            {
                model = "llama3",
                messages = new[] { new { role = "user", content = question } },
                stream = false
            }))
        };
        request.Headers.Add("X-InferHub-Retrieve", collection);
        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static ValidateOptionsResult Validate(LocalRetrievalOptions options, bool meshed = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:Enabled"] = meshed ? "true" : "false",
                ["LocalApi:Enabled"] = "true"
            })
            .Build();

        return new LocalRetrievalOptionsValidator(configuration).Validate(null, options);
    }
}
