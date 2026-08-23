using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Gemini;

namespace InferHub.Tests;

/// <summary>
/// Phase 64. Gemini's own <c>:generateContent</c>, driven entirely against <b>recorded payloads</b>
/// — the shapes in this file are the ones the vendor's documentation carries, captured on the day
/// the dialect was written.
/// </summary>
/// <remarks>
/// The track's D6: no test in this repository calls a provider. A test that needs somebody's API
/// key is a test CI cannot run, which makes it a test everyone learns to skip, and it bills a card
/// on every commit. The first real key is phase 68's.
/// </remarks>
public class GeminiDialectTests
{
    private const string ChatRequest = """
    {"model":"gemini-3-pro","messages":[{"role":"system","content":"Be terse."},{"role":"user","content":"hi"}]}
    """;

    /// <summary>
    /// A complete non-streamed answer. <b>Note the counts:</b> <c>promptTokenCount</c> is 165 and
    /// <c>cachedContentTokenCount</c> 140 — the cached tokens are <em>inside</em> the prompt count
    /// here, where Anthropic reports them beside it (64 D5). <c>thoughtsTokenCount</c> is separate
    /// from the answer's 7 and is billed as output (64 D6).
    /// </summary>
    private const string GenerateResponse = """
    {"candidates":[{"content":{"role":"model","parts":[{"text":"Hello!"}]},"finishReason":"STOP","index":0}],
     "usageMetadata":{"promptTokenCount":165,"cachedContentTokenCount":140,"candidatesTokenCount":7,
                      "thoughtsTokenCount":12,"totalTokenCount":184},
     "modelVersion":"gemini-3-pro","responseId":"rid_01"}
    """;

    /// <summary>Google's envelope: a <b>numeric</b> code, a canonical status, and typed details.</summary>
    private const string QuotaErrorBody = """
    {"error":{"code":429,"message":"Quota exceeded for quota metric 'Generate requests'.",
     "status":"RESOURCE_EXHAUSTED",
     "details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generate"}]},
                {"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"51s"}]}}
    """;

    /// <summary>The standard 1×1 PNG. Its magic bytes are what <c>Base64MediaType</c> reads.</summary>
    private const string PngPixel =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // ---- 64 D2: the model is a path segment ------------------------------------------------

    [Theory]
    [InlineData("gemini-3-pro", "models/gemini-3-pro:generateContent")]
    [InlineData("models/gemini-3-pro", "models/gemini-3-pro:generateContent")]
    [InlineData("publishers/google/models/gemini-3-pro", "publishers/google/models/gemini-3-pro:generateContent")]
    public async Task TheModelIsAPathSegmentAndTheThreeLegalFormsEachReachTheUrlTheyMean(
        string configured,
        string expectedPath)
    {
        // One rule for three forms (64 D2): an id that already contains a slash is a path and is
        // used as written; a bare one gets the prefix. Passing `models/gemini-3-pro` through
        // unchanged would produce `models/models/gemini-3-pro` — a 404 naming a model nobody typed.
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder).ChatAsync(
            $$"""{"model":"{{configured}}","messages":[{"role":"user","content":"hi"}]}""",
            CancellationToken.None);

        Assert.Equal($"https://generativelanguage.googleapis.com/v1beta/{expectedPath}", recorder.LastUri!.ToString());
    }

    [Fact]
    public async Task TheCredentialIsXGoogApiKeyAndThereIsNoAuthorizationHeader()
    {
        // A Bearer token sent here is a 401 that reads like a bad key — 63 D1's trap, Google's
        // spelling, and the third credential shape this seam has met.
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder).ChatAsync(ChatRequest, CancellationToken.None);

        Assert.Equal("key", Assert.Single(recorder.LastHeaders!.GetValues("x-goog-api-key")));
        Assert.False(recorder.LastHeaders!.Contains("Authorization"));
    }

    // ---- the request ------------------------------------------------------------------------

    [Fact]
    public async Task ASystemTurnBecomesSystemInstructionAndTheRolesAreGeminis()
    {
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder).ChatAsync(
            """
            {"model":"gemini-3-pro","messages":[{"role":"system","content":"Be terse."},
             {"role":"user","content":"hi"},{"role":"assistant","content":"Hi."},{"role":"user","content":"more"}]}
            """,
            CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        var root = sent.RootElement;

        Assert.Equal(
            "Be terse.",
            root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());

        var contents = root.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());

        // Gemini's assistant is called "model", and it is the one place in this project where it is.
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("user", contents[2].GetProperty("role").GetString());
    }

    [Fact]
    public async Task EverySamplingOptionSurvivesHereIncludingTheThreeAnthropicHadToDrop()
    {
        // The contrast with 63 D4 that is worth pinning: Gemini's generationConfig has `seed`,
        // `presencePenalty` and `frequencyPenalty`, so Anthropic's drop was a fact about that
        // vendor rather than a policy of this house.
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder).ChatAsync(
            """
            {"model":"gemini-3-pro","messages":[{"role":"user","content":"hi"}],
             "options":{"num_predict":128,"temperature":0.2,"top_p":0.9,"top_k":40,"seed":7,
                        "presence_penalty":0.5,"frequency_penalty":1.5,"stop":["END"]}}
            """,
            CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        var config = sent.RootElement.GetProperty("generationConfig");

        Assert.Equal(128, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(0.2, config.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(40, config.GetProperty("topK").GetInt32());
        Assert.Equal(7, config.GetProperty("seed").GetInt32());
        Assert.Equal(0.5, config.GetProperty("presencePenalty").GetDouble(), 3);
        Assert.Equal(1.5, config.GetProperty("frequencyPenalty").GetDouble(), 3);
        Assert.Equal("END", config.GetProperty("stopSequences")[0].GetString());
    }

    [Fact]
    public async Task NoCeilingIsImposedWhenTheCallerNamedNoneAndNoThinkingBudgetIsInventedEither()
    {
        // 64 D6. Gemini does not require maxOutputTokens, so 63 D2's declared default is
        // deliberately not reused: a ceiling nobody asked for is a truncated answer nobody can
        // explain. And an absent ThinkingBudget leaves the vendor's own dynamic default alone —
        // turning off a model's reasoning is a quality decision no operator made here.
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder, thinkingBudget: null).ChatAsync(ChatRequest, CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);

        Assert.False(sent.RootElement.TryGetProperty("generationConfig", out _));
    }

    [Fact]
    public async Task AThinkingBudgetTravelsWhenTheOperatorDeclaredOne()
    {
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder, thinkingBudget: 0).ChatAsync(ChatRequest, CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);

        Assert.Equal(
            0,
            sent.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig")
                .GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public async Task AnImageBecomesInlineDataWithAMimeTypeReadFromItsMagicBytes()
    {
        var recorder = Records(HttpStatusCode.OK, GenerateResponse);

        await Client(recorder).ChatAsync(
            $$"""
            {"model":"gemini-3-pro","messages":[{"role":"user","content":"what is this","images":["{{PngPixel}}"]}]}
            """,
            CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        var parts = sent.RootElement.GetProperty("contents")[0].GetProperty("parts");

        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("image/png", parts[0].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(PngPixel, parts[0].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("what is this", parts[1].GetProperty("text").GetString());
    }

    // ---- the counts, which are the phase's two counting decisions ---------------------------

    [Fact]
    public async Task TheCachedTokensStayInsideThePromptCountAndTheThinkingTokensStayOutOfTheAnswers()
    {
        // 64 D5 and D6 in one assertion, and it is the pair that would be wrong in opposite
        // directions if either were "just added up": promptTokenCount already contains the 140
        // cached tokens, and the 12 thinking tokens are not part of the 7-token answer.
        var response = await Client(Records(HttpStatusCode.OK, GenerateResponse))
            .ChatAsync(ChatRequest, CancellationToken.None);

        using var ollama = JsonDocument.Parse(response);
        var root = ollama.RootElement;

        Assert.Equal("Hello!", root.GetProperty("message").GetProperty("content").GetString());
        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.Equal("stop", root.GetProperty("done_reason").GetString());

        Assert.Equal(165, root.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(7, root.GetProperty("eval_count").GetInt32());
    }

    // ---- the stream, which is a third end-of-stream convention -------------------------------

    [Fact]
    public async Task TheStreamingUrlCarriesAltSseBecauseWithoutItTheEndpointIsNotSseAtAll()
    {
        // 64 D3. Without the query the vendor answers with a chunked JSON array and never emits a
        // `data:` line — a reader written for SSE does not fail, it waits for the timeout. That is
        // why the query is a constant beside the verb rather than a setting.
        var recorder = RespondsWithStream(
            Frame("""{"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]}}]}"""));

        await Collect(Client(recorder));

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro:streamGenerateContent?alt=sse",
            recorder.LastUri!.ToString());
    }

    [Fact]
    public async Task AStreamsCountsAreTakenFromTheLastFrameRatherThanSummed()
    {
        // The load-bearing assertion. Gemini's streaming usageMetadata is CUMULATIVE, so a dialect
        // that summed the three frames would report 2 + 9 + 15 = 26 for a fifteen-token answer —
        // the same mistake 63 D5 refused at Anthropic, which is what makes it a rule (64 D4). A
        // frame with no candidates and one this release cannot parse are in the stream on purpose.
        var chunks = await Collect(Client(RespondsWithStream(
            Frame("""{"candidates":[{"content":{"role":"model","parts":[{"text":"Hel"}]}}],"usageMetadata":{"promptTokenCount":165,"candidatesTokenCount":2,"totalTokenCount":167}}"""),
            Frame("""{"candidates":[{"content":{"role":"model","parts":[]}}]}"""),
            Frame("""{"candidates":[{"content":{"role":"model","parts":[{"text":"lo"}]}}],"usageMetadata":{"promptTokenCount":165,"candidatesTokenCount":9,"totalTokenCount":174}}"""),
            Frame("""{"weatherReport":"a frame shape this release has never seen"}"""),
            Frame("""{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":165,"candidatesTokenCount":15,"thoughtsTokenCount":40,"totalTokenCount":220}}"""))));

        // Two text deltas and one done chunk. The empty-parts frames produce nothing — an empty
        // delta is something a client would have to filter.
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hel", Text(chunks[0]));
        Assert.Equal("lo", Text(chunks[1]));

        using var done = JsonDocument.Parse(chunks[2]);
        Assert.True(done.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal("stop", done.RootElement.GetProperty("done_reason").GetString());
        Assert.Equal(165, done.RootElement.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(15, done.RootElement.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public async Task AStreamThatCarriesNoUsageAtAllReportsNoCountsRatherThanZeros()
    {
        // A zero you constructed to fill a field is not a measurement (v3.13.1), and it is easiest
        // to break in exactly the code that argues for it.
        var chunks = await Collect(Client(RespondsWithStream(
            Frame("""{"candidates":[{"content":{"role":"model","parts":[{"text":"hi"}]},"finishReason":"STOP"}]}"""))));

        using var done = JsonDocument.Parse(chunks[^1]);

        Assert.False(done.RootElement.TryGetProperty("prompt_eval_count", out _));
        Assert.False(done.RootElement.TryGetProperty("eval_count", out _));
    }

    [Fact]
    public async Task AMaxTokensFinishBecomesOllamasLengthSoAClientCanSeeItWasCutOff()
    {
        var chunks = await Collect(Client(RespondsWithStream(
            Frame("""{"candidates":[{"content":{"role":"model","parts":[{"text":"a"}]}}]}"""),
            Frame("""{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"MAX_TOKENS"}]}"""))));

        using var done = JsonDocument.Parse(chunks[^1]);
        Assert.Equal("length", done.RootElement.GetProperty("done_reason").GetString());
    }

    [Fact]
    public async Task AnErrorFrameIsRaisedRatherThanEndingTheStreamQuietly()
    {
        // 62 D4's contract for the third time. A failure that arrives after the response headers
        // would otherwise be a 200 that looks finished.
        var client = Client(RespondsWithStream(
            Frame("""{"candidates":[{"content":{"role":"model","parts":[{"text":"Hel"}]}}]}"""),
            Frame("""{"error":{"code":503,"message":"The model is overloaded.","status":"UNAVAILABLE"}}""")));

        var delivered = new List<string>();

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(async () =>
        {
            await foreach (var chunk in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
            {
                delivered.Add(chunk);
            }
        });

        Assert.Equal(502, failure.StatusCode);
        Assert.Contains("UNAVAILABLE", failure.Message);
        Assert.Contains("The model is overloaded.", failure.Message);

        // What arrived before the failure is still what arrived, and no done chunk was appended.
        Assert.Single(delivered);
    }

    // ---- 64 D7: the third "200 that looks finished" this track has found ---------------------

    [Fact]
    public async Task ABlockedPromptIsAnErrorRatherThanAnEmptyAnswerThatLooksFinished()
    {
        var client = Client(Records(
            HttpStatusCode.OK,
            """{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"},"usageMetadata":{"promptTokenCount":8,"totalTokenCount":8}}"""));

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Equal(502, failure.StatusCode);
        Assert.Contains("PROHIBITED_CONTENT", failure.Message);
        Assert.Contains("refused the prompt", failure.Message);
    }

    [Fact]
    public async Task ABlockedPromptMidStreamThrowsTheSameWay()
    {
        var client = Client(RespondsWithStream(
            Frame("""{"promptFeedback":{"blockReason":"SAFETY"}}""")));

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
            {
            }
        });

        Assert.Contains("SAFETY", failure.Message);
    }

    [Fact]
    public async Task AStreamingResponseThatIsNotSseAtAllThrowsRatherThanYieldingAnEmptyAnswer()
    {
        // v3.32.1, and it was found by running the published image rather than by reasoning.
        // A body with no `data:` lines used to have every line skipped, the loop end, and the
        // ordinary done chunk emitted — an empty answer marked done:true, which is the fourth
        // time this track has met a success that is not one and the first time we shipped it.
        //
        // Two real bodies land here: a block delivered as a plain response on the streaming
        // endpoint, and the JSON array the endpoint returns when `alt=sse` does not reach it
        // (through a proxy that drops the query, say). 64 D3 claimed the second case would hang
        // until the timeout. It does not — it answers immediately, wrongly, which is worse.
        var client = Client(Records(
            HttpStatusCode.OK,
            """{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"},"usageMetadata":{"promptTokenCount":8}}"""));

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
            {
            }
        });

        // The reason survives the discovery that the transport was wrong: a caller needs to know
        // their prompt was refused, not merely that the framing surprised us.
        Assert.Contains("PROHIBITED_CONTENT", failure.Message);
    }

    [Fact]
    public async Task AStreamThatArrivesAsAJsonArrayIsRefusedAndNamesTheMissingAltSse()
    {
        // What `:streamGenerateContent` returns without `alt=sse`. The client always sends it, so
        // reaching this means something between here and the vendor dropped the query string —
        // and the operator needs to be told that, not handed an empty answer.
        var client = Client(Records(
            HttpStatusCode.OK,
            """[{"candidates":[{"content":{"role":"model","parts":[{"text":"Hello"}]}}]},{"candidates":[{"content":{"parts":[]},"finishReason":"STOP"}]}]"""));

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(async () =>
        {
            await foreach (var _ in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
            {
            }
        });

        Assert.Contains("alt=sse", failure.Message);
    }

    // ---- errors ------------------------------------------------------------------------------

    [Fact]
    public async Task AFailureCarriesGooglesMessageItsCanonicalStatusAndItsRetryDelay()
    {
        // 64 D9. `status` is the half an HTTP number does not give you and `retryDelay` is the half
        // a human needs — the two fields a compatibility layer drops, and Gemini's equivalent of
        // the request_id 63 carried through.
        var client = Client(Records(HttpStatusCode.TooManyRequests, QuotaErrorBody));

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Equal(429, failure.StatusCode);
        Assert.Contains("RESOURCE_EXHAUSTED", failure.Message);
        Assert.Contains("Quota exceeded", failure.Message);
        Assert.Contains("retry after 51s", failure.Message);
        Assert.DoesNotContain("{", failure.Message);
    }

    [Fact]
    public async Task AnErrorBodyInNoKnownShapeStillReachesTheOperatorWhole()
    {
        // 29 D6: it unwraps, it never infers. A proxy in front of the vendor answers in its own
        // dialect and gets its body forwarded.
        var client = Client(Records(HttpStatusCode.BadGateway, """{"detail":"upstream connect error"}"""));

        var failure = await Assert.ThrowsAsync<GeminiUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Contains("upstream connect error", failure.Message);
    }

    // ---- 64 D8: the first dialect whose embeddings are real ----------------------------------

    [Fact]
    public async Task EmbeddingsAreRealHereAndNoTaskTypeIsEverSent()
    {
        // The counterpart to 63 D7's 501, and the same seam answered by the other vendor. No
        // taskType: RETRIEVAL_DOCUMENT and RETRIEVAL_QUERY produce better vectors and nothing in
        // Ollama's /api/embed says which one a caller means.
        var recorder = Records(
            HttpStatusCode.OK,
            """{"embeddings":[{"values":[0.1,0.2]},{"values":[0.3,0.4]}]}""");

        var response = await Client(recorder).EmbedAsync(
            """{"model":"models/gemini-embedding-001","input":["one","two"]}""",
            CancellationToken.None);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-001:batchEmbedContents",
            recorder.LastUri!.ToString());

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        var requests = sent.RootElement.GetProperty("requests");

        Assert.Equal(2, requests.GetArrayLength());
        Assert.Equal("models/gemini-embedding-001", requests[0].GetProperty("model").GetString());
        Assert.Equal("one", requests[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.False(requests[0].TryGetProperty("taskType", out _));

        using var ollama = JsonDocument.Parse(response);
        var vectors = ollama.RootElement.GetProperty("embeddings");

        Assert.Equal(2, vectors.GetArrayLength());
        Assert.Equal(0.3f, vectors[1][0].GetSingle(), 4);
    }

    [Fact]
    public async Task ASingleInputTakesTheSameBatchPathRatherThanASecondOne()
    {
        // 64 D8: one code path is worth more than a saved field.
        var recorder = Records(HttpStatusCode.OK, """{"embeddings":[{"values":[0.1]}]}""");

        await Client(recorder).EmbedAsync(
            """{"model":"gemini-embedding-001","input":"one"}""",
            CancellationToken.None);

        Assert.EndsWith(":batchEmbedContents", recorder.LastUri!.ToString());

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        Assert.Equal(1, sent.RootElement.GetProperty("requests").GetArrayLength());
    }

    // ---- discovery ---------------------------------------------------------------------------

    [Fact]
    public async Task TheModelListingComesBackInTheVendorsOwnFormSoItIsPasteableIntoAModelMap()
    {
        var client = Client(Records(
            HttpStatusCode.OK,
            """{"models":[{"name":"models/gemini-3-pro","displayName":"Gemini 3 Pro"},{"name":"models/gemini-embedding-001"}]}"""));

        var ids = await client.ListModelIdsAsync(CancellationToken.None);

        Assert.Equal(["models/gemini-3-pro", "models/gemini-embedding-001"], ids);
    }

    // ---- harness -----------------------------------------------------------------------------

    /// <summary>
    /// One SSE frame. Gemini names no events — every frame is a whole
    /// <c>GenerateContentResponse</c> on a bare <c>data:</c> line, and there is no <c>[DONE]</c>.
    /// </summary>
    private static string Frame(string json) => $"data: {json}\n\n";

    private static string Text(string chunk)
    {
        using var document = JsonDocument.Parse(chunk);
        return document.RootElement.GetProperty("message").GetProperty("content").GetString()!;
    }

    private static async Task<List<string>> Collect(GeminiUpstreamClient client)
    {
        var chunks = new List<string>();

        await foreach (var chunk in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static GeminiUpstreamClient Client(HttpMessageHandler handler, int? thinkingBudget = null)
        => new(
            GeminiUpstreamClient.Configure(
                new HttpClient(handler, disposeHandler: false),
                "https://generativelanguage.googleapis.com/v1beta",
                "key",
                timeoutSeconds: 30),
            thinkingBudget);

    private static StubHandler Records(HttpStatusCode status, string body)
        => new(status, body, "application/json");

    private static StubHandler RespondsWithStream(params string[] frames)
        => new(HttpStatusCode.OK, string.Concat(frames), "text/event-stream");

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public Uri? LastUri { get; private set; }

        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastHeaders = request.Headers;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    Headers = { { "Content-Type", contentType } }
                }
            };
        }
    }
}
