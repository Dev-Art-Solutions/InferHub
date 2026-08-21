using System.Net;
using System.Text;
using InferHub.Shared.OpenAi;

namespace InferHub.Tests;

/// <summary>
/// Phase 62. The two places an OpenAI-compatible upstream is not OpenAI, driven against
/// <b>recorded payloads</b> — a captured OpenRouter 429 and a captured mid-stream error frame.
/// </summary>
/// <remarks>
/// The track's D6: no test in this repository calls a provider. A test that needs somebody's API key
/// is a test CI cannot run, which makes it a test everyone learns to skip, and it bills a card on
/// every commit. `NodeErrorReadabilityTests` pinning a real Ollama payload is the precedent.
/// </remarks>
public class UpstreamDialectTests
{
    private const string ChatRequest = """
    {"model":"qwen/qwen3-coder","messages":[{"role":"user","content":"hi"}]}
    """;

    /// <summary>OpenRouter, verbatim: the status is repeated into <c>code</c> as a number.</summary>
    private const string NumericCodeError = """
    {"error":{"code":429,"message":"Rate limit exceeded: free-models-per-day","metadata":{"error_type":"rate_limit_exceeded","provider_code":"429"}}}
    """;

    /// <summary>OpenAI, verbatim: the same field, a string.</summary>
    private const string StringCodeError = """
    {"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","param":null,"code":"invalid_api_key"}}
    """;

    // ---- the error envelope both vendors spell differently -------------------------------

    [Fact]
    public async Task ANumericErrorCodeStillYieldsTheUpstreamsOwnMessage()
    {
        // Before this, deserializing the envelope threw, Describe caught its own exception and fell
        // back to the raw body — the one sentence saying what to fix, buried in the JSON it arrived
        // in. 29 D6's wall of backslashes, by another route.
        var client = Client(Responds(HttpStatusCode.TooManyRequests, NumericCodeError));

        var failure = await Assert.ThrowsAsync<OpenAiUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Equal(429, failure.StatusCode);
        Assert.Contains("Rate limit exceeded: free-models-per-day", failure.Message);
        Assert.DoesNotContain("metadata", failure.Message);
        Assert.DoesNotContain("{", failure.Message);
    }

    [Fact]
    public async Task AStringErrorCodeReadsExactlyAsItAlwaysDid()
    {
        // Guard on the guard: the fix must not be a second parser that only the new shape survives.
        var client = Client(Responds(HttpStatusCode.Unauthorized, StringCodeError));

        var failure = await Assert.ThrowsAsync<OpenAiUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Equal(401, failure.StatusCode);
        Assert.Contains("Incorrect API key provided.", failure.Message);
    }

    [Fact]
    public async Task AnErrorBodyInNoKnownShapeStillReachesTheOperatorWhole()
    {
        // 29 D6: it unwraps, it never infers. A server that answers in its own dialect gets its body
        // forwarded rather than a sentence this project made up about it.
        var client = Client(Responds(HttpStatusCode.BadGateway, """{"detail":"upstream connect error"}"""));

        var failure = await Assert.ThrowsAsync<OpenAiUpstreamException>(
            () => client.ChatAsync(ChatRequest, CancellationToken.None));

        Assert.Contains("upstream connect error", failure.Message);
    }

    // ---- a failure that arrives after the headers ----------------------------------------

    [Fact]
    public async Task AMidStreamErrorIsRaisedRatherThanEndingTheStreamQuietly()
    {
        // The frame carries finish_reason "error" and the error object at the top level, so the old
        // parse read it as a terminal delta: a request that died at token 40 returned 200 and looked
        // finished. That is the failure this whole track exists to make impossible to have silently.
        var client = Client(RespondsWithStream(
            Frame("""{"id":"gen-1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}]}"""),
            Frame("""{"id":"gen-1","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"error"}],"error":{"code":502,"message":"Provider returned error"}}""")));

        var delivered = new List<string>();

        var failure = await Assert.ThrowsAsync<OpenAiUpstreamException>(async () =>
        {
            await foreach (var chunk in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
            {
                delivered.Add(chunk);
            }
        });

        Assert.Equal(502, failure.StatusCode);
        Assert.Contains("Provider returned error", failure.Message);

        // What arrived before the failure is still what arrived — the throw ends the stream, it does
        // not retract it.
        Assert.Single(delivered);
    }

    [Fact]
    public async Task AnOrdinaryStreamStillEndsWithADoneChunkAndNothingElse()
    {
        // Guard on the guard. The frames below mention the word this check keys on, in content,
        // which is the false positive that would turn every answer about error handling into a 502.
        var client = Client(RespondsWithStream(
            Frame("""{"id":"g","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"the \"error\" field"},"finish_reason":null}]}"""),
            Frame("""{"id":"g","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""),
            "data: [DONE]\n\n"));

        var delivered = new List<string>();

        await foreach (var chunk in client.StreamAsync("chat", ChatRequest, CancellationToken.None))
        {
            delivered.Add(chunk);
        }

        Assert.Equal(2, delivered.Count);
        Assert.Contains("the \\u0022error\\u0022 field", delivered[0]);
        Assert.Contains("\"done\":true", delivered[1]);
    }

    // ---- harness -------------------------------------------------------------------------

    private static string Frame(string json) => $"data: {json}\n\n";

    private static OpenAiUpstreamClient Client(HttpMessageHandler handler)
        => new(OpenAiUpstreamClient.Configure(
            new HttpClient(handler, disposeHandler: false),
            "https://openrouter.ai/api/v1",
            "key",
            timeoutSeconds: 30));

    private static HttpMessageHandler Responds(HttpStatusCode status, string body)
        => new StubHandler(status, body, "application/json");

    private static HttpMessageHandler RespondsWithStream(params string[] frames)
        => new StubHandler(HttpStatusCode.OK, string.Concat(frames), "text/event-stream");

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    Headers = { { "Content-Type", contentType } }
                }
            });
    }
}
