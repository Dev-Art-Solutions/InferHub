using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Anthropic;

namespace InferHub.Tests;

/// <summary>
/// Phase 63. Anthropic's own <c>/v1/messages</c>, driven entirely against <b>recorded payloads</b>
/// — the shapes in this file are the ones the vendor's documentation carries, captured on the day
/// the dialect was written.
/// </summary>
/// <remarks>
/// The track's D6: no test in this repository calls a provider. A test that needs somebody's API
/// key is a test CI cannot run, which makes it a test everyone learns to skip, and it bills a card
/// on every commit. The first real key is phase 68's.
/// </remarks>
public class AnthropicDialectTests
{
    private const string ChatRequest = """
    {"model":"claude-opus-5","messages":[{"role":"system","content":"Be terse."},{"role":"user","content":"hi"}]}
    """;

    /// <summary>A complete non-streamed answer, in Anthropic's shape rather than OpenAI's.</summary>
    private const string MessageResponse = """
    {"id":"msg_01","type":"message","role":"assistant","content":[{"type":"text","text":"Hello!"}],
     "model":"claude-opus-5","stop_reason":"end_turn","stop_sequence":null,
     "usage":{"input_tokens":25,"output_tokens":7,"cache_creation_input_tokens":0,"cache_read_input_tokens":140}}
    """;

    /// <summary>Anthropic's error envelope: a top-level <c>type</c>, and a <c>request_id</c> beside the body.</summary>
    private const string ErrorBody = """
    {"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: must be greater than 0"},
     "request_id":"req_011CSHoEeqs5C35K2UUqR7Fy"}
    """;

    // ---- the request Anthropic actually requires ------------------------------------------

    [Fact]
    public async Task TheRequiredMaxTokensIsSuppliedAndASystemTurnIsLifted()
    {
        // 63 D2 and D3. max_tokens is required by the vendor and Ollama has no equivalent; the
        // messages array has exactly two roles, so a system turn cannot be sent as one.
        var recorder = Records(HttpStatusCode.OK, MessageResponse);

        await Client(recorder, maxTokens: 4096).ChatAsync(ChatRequest, CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        var root = sent.RootElement;

        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("Be terse.", root.GetProperty("system").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public async Task ACallersNumPredictWinsAndTheOptionsAnthropicLacksAreDropped()
    {
        // 63 D4. Forwarding seed/presence_penalty/frequency_penalty is a 400 from the vendor, so
        // "drop" is not laziness — refusing instead would break every client that sets one.
        var recorder = Records(HttpStatusCode.OK, MessageResponse);

        await Client(recorder, maxTokens: 4096).ChatAsync(
            """
            {"model":"claude-opus-5","messages":[{"role":"user","content":"hi"}],
             "options":{"num_predict":128,"temperature":0.2,"top_k":40,"seed":7,"frequency_penalty":1.5,"stop":["END"]}}
            """,
            CancellationToken.None);

        using var sent = JsonDocument.Parse(recorder.LastBody!);
        var root = sent.RootElement;

        Assert.Equal(128, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(40, root.GetProperty("top_k").GetInt32());
        Assert.Equal("END", root.GetProperty("stop_sequences")[0].GetString());
        Assert.False(root.TryGetProperty("seed", out _));
        Assert.False(root.TryGetProperty("frequency_penalty", out _));
        Assert.False(root.TryGetProperty("presence_penalty", out _));
    }

    [Fact]
    public async Task TheCredentialIsXApiKeyAndTheVersionHeaderIsAlwaysSent()
    {
        // A Bearer token sent here is a 401 that reads like a bad key, which is why the dialect
        // configures its own client rather than inheriting the OpenAI one's (63 D1).
        var recorder = Records(HttpStatusCode.OK, MessageResponse);

        await Client(recorder, maxTokens: 4096).ChatAsync(ChatRequest, CancellationToken.None);

        Assert.Equal("key", Assert.Single(recorder.LastHeaders!.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(recorder.LastHeaders!.GetValues("anthropic-version")));
        Assert.False(recorder.LastHeaders!.Contains("Authorization"));
    }

    [Fact]
    public async Task TheAnswerComesBackAsOllamaJsonWithTheVendorsOwnCounts()
    {
        var response = await Client(Records(HttpStatusCode.OK, MessageResponse), maxTokens: 4096)
            .ChatAsync(ChatRequest, CancellationToken.None);

        using var ollama = JsonDocument.Parse(response);
        var root = ollama.RootElement;

        Assert.Equal("Hello!", root.GetProperty("message").GetProperty("content").GetString());
        Assert.True(root.GetProperty("done").GetBoolean());
        Assert.Equal("stop", root.GetProperty("done_reason").GetString());

        // 63 D5: the cache pair is reported and priced separately, so 140 cache-read tokens do not
        // silently become part of a prompt count that would then match no line on the invoice.
        Assert.Equal(25, root.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(7, root.GetProperty("eval_count").GetInt32());
    }

    // ---- the stream, which is not OpenAI's in three ways -----------------------------------

    [Fact]
    public async Task AStreamEndsAtMessageStopAndItsCountsAreTakenRatherThanSummed()
    {
        // The load-bearing assertion of this phase. message_start already carries output_tokens: 1
        // and every message_delta's usage is CUMULATIVE, so a dialect that summed would report
        // 1 + 8 + 15 = 24 for a fifteen-token answer. A ping and an event type this release has
        // never seen are in the stream on purpose.
        var chunks = await Collect(Client(
            RespondsWithStream(
                Event("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude-opus-5","stop_reason":null,"usage":{"input_tokens":25,"output_tokens":1}}}"""),
                Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                Event("ping", """{"type":"ping"}"""),
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}"""),
                Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":null},"usage":{"output_tokens":8}}"""),
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}"""),
                Event("weather_report", """{"type":"weather_report","forecast":"an event type this release has never seen"}"""),
                Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":15}}"""),
                Event("message_stop", """{"type":"message_stop"}""")),
            maxTokens: 4096));

        // Two text deltas and one done chunk. The ping, the block boundaries and the unknown event
        // produce nothing — the vendor's versioning policy says new types will arrive.
        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hel", Text(chunks[0]));
        Assert.Equal("lo", Text(chunks[1]));

        using var done = JsonDocument.Parse(chunks[2]);
        Assert.True(done.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal("stop", done.RootElement.GetProperty("done_reason").GetString());
        Assert.Equal(25, done.RootElement.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(15, done.RootElement.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public async Task AStreamThatCarriesNoUsageAtAllReportsNoCountsRatherThanZeros()
    {
        // A zero you constructed to fill a field is not a measurement (v3.13.1), and it is easiest
        // to break in exactly the code that argues for it.
        var chunks = await Collect(Client(
            RespondsWithStream(
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}"""),
                Event("message_stop", """{"type":"message_stop"}""")),
            maxTokens: 4096));

        using var done = JsonDocument.Parse(chunks[^1]);

        Assert.False(done.RootElement.TryGetProperty("prompt_eval_count", out _));
        Assert.False(done.RootElement.TryGetProperty("eval_count", out _));
    }

    [Fact]
    public async Task AMaxTokensStopBecomesOllamasLengthSoAClientCanSeeItWasCutOff()
    {
        var chunks = await Collect(Client(
            RespondsWithStream(
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"a"}}"""),
                Event("message_delta", """{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":4096}}"""),
                Event("message_stop", """{"type":"message_stop"}""")),
            maxTokens: 4096));

        using var done = JsonDocument.Parse(chunks[^1]);
        Assert.Equal("length", done.RootElement.GetProperty("done_reason").GetString());
    }

    [Fact]
    public async Task AnErrorEventIsRaisedRatherThanEndingTheStreamQuietly()
    {
        // 62 D4's contract, reused. overloaded_error arrives after the response headers, so
        // without this a request that died at token 40 returns 200 and looks finished.
        var client = Client(
            RespondsWithStream(
                Event("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}"""),
                Event("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""")),
            maxTokens: 4096);

        var delivered = new List<string>();

        var failure = await Assert.ThrowsAsync<AnthropicUpstreamException>(async () =>
        {
            await foreach (var chunk in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
            {
                delivered.Add(chunk);
            }
        });

        Assert.Equal(502, failure.StatusCode);
        Assert.Contains("Overloaded", failure.Message);

        // What arrived before the failure is still what arrived — the throw ends the stream, it
        // does not retract it. And no done chunk was appended.
        Assert.Single(delivered);
    }

    // ---- errors ----------------------------------------------------------------------------

    [Fact]
    public async Task AFailureCarriesTheVendorsOwnMessageAndTheRequestIdSupportAsksFor()
    {
        var client = Client(Records(HttpStatusCode.BadRequest, ErrorBody), maxTokens: 4096);

        var failure = await Assert.ThrowsAsync<AnthropicUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Equal(400, failure.StatusCode);
        Assert.Contains("max_tokens: must be greater than 0", failure.Message);
        Assert.Contains("req_011CSHoEeqs5C35K2UUqR7Fy", failure.Message);
        Assert.DoesNotContain("{", failure.Message);
    }

    [Fact]
    public async Task A529IsNamedByItsNumberBecauseTheFrameworkHasNoNameForIt()
    {
        // overloaded_error is Anthropic's own status and is not in HttpStatusCode. Printing
        // "529 529" would be the framework's gap showing through to an operator.
        var client = Client(
            Records((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""),
            maxTokens: 4096);

        var failure = await Assert.ThrowsAsync<AnthropicUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Equal(529, failure.StatusCode);
        Assert.Contains("returned 529:", failure.Message);
        Assert.Contains("Overloaded", failure.Message);
    }

    [Fact]
    public async Task AnErrorBodyInNoKnownShapeStillReachesTheOperatorWhole()
    {
        // 29 D6: it unwraps, it never infers. A proxy in front of the vendor answers in its own
        // dialect and gets its body forwarded.
        var client = Client(
            Records(HttpStatusCode.BadGateway, """{"detail":"upstream connect error"}"""),
            maxTokens: 4096);

        var failure = await Assert.ThrowsAsync<AnthropicUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Contains("upstream connect error", failure.Message);
    }

    [Fact]
    public async Task ThereAreNoEmbeddingsAndTheRefusalSaysWhy()
    {
        // 63 D7. Not a 503 — "try later" for an endpoint that will never exist — and not an empty
        // vector, which is a wrong answer shaped like a right one.
        var client = Client(Records(HttpStatusCode.OK, MessageResponse), maxTokens: 4096);

        var failure = await Assert.ThrowsAsync<AnthropicUpstreamException>(
            () => client.EmbedAsync("""{"model":"claude-opus-5","input":"hi"}""", CancellationToken.None));

        Assert.Equal(501, failure.StatusCode);
        Assert.Contains("no embeddings API", failure.Message);
    }

    // ---- harness ---------------------------------------------------------------------------

    private static string Event(string name, string json) => $"event: {name}\ndata: {json}\n\n";

    private static string Text(string chunk)
    {
        using var document = JsonDocument.Parse(chunk);
        return document.RootElement.GetProperty("message").GetProperty("content").GetString()!;
    }

    private static async Task<List<string>> Collect(AnthropicUpstreamClient client)
    {
        var chunks = new List<string>();

        await foreach (var chunk in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static AnthropicUpstreamClient Client(HttpMessageHandler handler, int maxTokens)
        => new(
            AnthropicUpstreamClient.Configure(
                new HttpClient(handler, disposeHandler: false),
                "https://api.anthropic.com/v1",
                "key",
                timeoutSeconds: 30),
            maxTokens);

    private static StubHandler Records(HttpStatusCode status, string body)
        => new(status, body, "application/json");

    private static HttpMessageHandler RespondsWithStream(params string[] events)
        => new StubHandler(HttpStatusCode.OK, string.Concat(events), "text/event-stream");

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
