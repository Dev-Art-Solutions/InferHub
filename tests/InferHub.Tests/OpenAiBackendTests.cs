using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// The node's second backend, against a stub upstream. Every assertion here is about the two
/// translations: Ollama-shaped job in → OpenAI on the wire → Ollama-shaped response out. The
/// coordinator must never be able to tell which backend answered.
/// </summary>
public class OpenAiBackendTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- blocking --------------------------------------------------------------------

    [Fact]
    public async Task BlockingChatSendsOpenAiAndReturnsOllama()
    {
        var upstream = StubUpstream.Json("""
        {
          "id": "chatcmpl-1",
          "object": "chat.completion",
          "created": 1700000000,
          "model": "meta-llama/Llama-3.1-8B-Instruct",
          "choices": [
            {"index":0,"message":{"role":"assistant","content":"Hello there."},"finish_reason":"stop"}
          ],
          "usage": {"prompt_tokens": 11, "completion_tokens": 4, "total_tokens": 15}
        }
        """);

        var backend = Backend(upstream);

        var responseJson = await backend.ChatAsync("""
        {"model":"llama3","messages":[{"role":"user","content":"Hi!"}],"stream":false}
        """, CancellationToken.None);

        // What went upstream is OpenAI-shaped.
        var sent = upstream.LastRequestBody();
        Assert.Equal("/v1/chat/completions", upstream.LastRequestPath);
        Assert.Equal("llama3", sent.GetProperty("model").GetString());
        Assert.Equal("Hi!", sent.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(sent.GetProperty("stream").GetBoolean());

        // What comes back is Ollama-shaped — this is what the coordinator will parse.
        var ollama = Parse(responseJson);
        Assert.Equal("Hello there.", ollama.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("assistant", ollama.GetProperty("message").GetProperty("role").GetString());
        Assert.True(ollama.GetProperty("done").GetBoolean());
        Assert.Equal("stop", ollama.GetProperty("done_reason").GetString());

        // Token counts have to survive the trip: phase 25 meters off them.
        Assert.Equal(11, ollama.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(4, ollama.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public async Task OllamaSamplingOptionsBecomeOpenAiParameters()
    {
        var upstream = StubUpstream.Json("""
        {"id":"c","created":0,"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"x"},"finish_reason":"stop"}]}
        """);

        await Backend(upstream).ChatAsync("""
        {
          "model": "llama3",
          "messages": [{"role":"user","content":"hi"}],
          "options": {"temperature":0.4,"top_p":0.9,"seed":7,"num_predict":128,"stop":["END"]}
        }
        """, CancellationToken.None);

        var sent = upstream.LastRequestBody();
        Assert.Equal(0.4, sent.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.9, sent.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(7, sent.GetProperty("seed").GetInt64());
        Assert.Equal(128, sent.GetProperty("max_tokens").GetInt32());
        Assert.Equal("END", sent.GetProperty("stop")[0].GetString());
    }

    [Fact]
    public async Task BlockingGenerateUsesTheCompletionsRoute()
    {
        var upstream = StubUpstream.Json("""
        {
          "id": "cmpl-1",
          "created": 0,
          "model": "llama3",
          "choices": [{"index":0,"text":"...and they lived.","finish_reason":"length"}],
          "usage": {"prompt_tokens": 5, "completion_tokens": 6, "total_tokens": 11}
        }
        """);

        var responseJson = await Backend(upstream).GenerateAsync("""
        {"model":"llama3","prompt":"Once upon a time","stream":false}
        """, CancellationToken.None);

        Assert.Equal("/v1/completions", upstream.LastRequestPath);
        Assert.Equal("Once upon a time", upstream.LastRequestBody().GetProperty("prompt").GetString());

        var ollama = Parse(responseJson);
        Assert.Equal("...and they lived.", ollama.GetProperty("response").GetString());
        Assert.True(ollama.GetProperty("done").GetBoolean());
        Assert.Equal("length", ollama.GetProperty("done_reason").GetString());
        Assert.Equal(6, ollama.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public async Task ToolCallArgumentsAreRehydratedIntoAnObjectForOllama()
    {
        // OpenAI carries tool-call arguments as a JSON *string*; Ollama carries them as an
        // object. Handing the string through unchanged would break every Ollama-side consumer.
        var upstream = StubUpstream.Json("""
        {
          "id": "chatcmpl-1", "created": 0, "model": "llama3",
          "choices": [{
            "index": 0,
            "message": {
              "role": "assistant",
              "content": "",
              "tool_calls": [{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Sofia\"}"}}]
            },
            "finish_reason": "tool_calls"
          }]
        }
        """);

        var responseJson = await Backend(upstream).ChatAsync("""
        {"model":"llama3","messages":[{"role":"user","content":"weather?"}]}
        """, CancellationToken.None);

        var call = Parse(responseJson)
            .GetProperty("message")
            .GetProperty("tool_calls")[0];

        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());

        var arguments = call.GetProperty("function").GetProperty("arguments");
        Assert.Equal(JsonValueKind.Object, arguments.ValueKind);
        Assert.Equal("Sofia", arguments.GetProperty("city").GetString());
    }

    // ---- streaming -------------------------------------------------------------------

    [Fact]
    public async Task StreamingChatYieldsDeltasThenOneDoneChunkCarryingUsage()
    {
        var upstream = StubUpstream.Sse(
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{"role":"assistant","content":"He"},"finish_reason":null}]}""",
            ": keep-alive",
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{"content":"llo"},"finish_reason":null}]}""",
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            """data: {"id":"c","created":0,"model":"llama3","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""",
            "data: [DONE]");

        var chunks = await Collect(Backend(upstream).StreamAsync(
            "chat",
            """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            CancellationToken.None));

        // Two content deltas and exactly one terminal chunk.
        Assert.Equal(3, chunks.Count);

        Assert.Equal("He", chunks[0].GetProperty("message").GetProperty("content").GetString());
        Assert.False(chunks[0].GetProperty("done").GetBoolean());
        Assert.Equal("llo", chunks[1].GetProperty("message").GetProperty("content").GetString());
        Assert.False(chunks[1].GetProperty("done").GetBoolean());

        var done = chunks[2];
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.Equal("stop", done.GetProperty("done_reason").GetString());

        // The counts arrive in a usage chunk *after* finish_reason. Emitting the done chunk the
        // moment finish_reason lands would drop them silently.
        Assert.Equal(3, done.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(2, done.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public async Task StreamingRequestsUsageFromTheUpstream()
    {
        var upstream = StubUpstream.Sse("data: [DONE]");

        await Collect(Backend(upstream).StreamAsync(
            "chat",
            """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            CancellationToken.None));

        var sent = upstream.LastRequestBody();
        Assert.True(sent.GetProperty("stream").GetBoolean());
        Assert.True(sent.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task AStreamThatEndsWithoutUsageStillTerminates()
    {
        // llama.cpp's server may ignore stream_options. A missing usage chunk means missing
        // token counts, not a stream that never closes.
        var upstream = StubUpstream.Sse(
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{"content":"hi"},"finish_reason":null}]}""",
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "data: [DONE]");

        var chunks = await Collect(Backend(upstream).StreamAsync(
            "chat",
            """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}""",
            CancellationToken.None));

        var done = chunks[^1];
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.False(done.TryGetProperty("prompt_eval_count", out _));
    }

    [Fact]
    public async Task StreamingGenerateYieldsOllamaGenerateChunks()
    {
        var upstream = StubUpstream.Sse(
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"text":"once","finish_reason":null}]}""",
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"text":"","finish_reason":"stop"}]}""",
            "data: [DONE]");

        var chunks = await Collect(Backend(upstream).StreamAsync(
            "generate",
            """{"model":"llama3","prompt":"tell me","stream":true}""",
            CancellationToken.None));

        Assert.Equal("once", chunks[0].GetProperty("response").GetString());
        Assert.False(chunks[0].GetProperty("done").GetBoolean());
        Assert.True(chunks[^1].GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task AnUpstreamErrorMidStreamThrowsRatherThanTruncatingSilently()
    {
        // InferenceExecutor turns a throw into a failed chunk — the same contract the Ollama
        // backend honours. A silent truncation would look like a complete answer.
        var upstream = StubUpstream.Status(HttpStatusCode.InternalServerError, "boom");

        await Assert.ThrowsAsync<OpenAiUpstreamException>(async () => await Collect(
            Backend(upstream).StreamAsync(
                "chat",
                """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}""",
                CancellationToken.None)));
    }

    [Fact]
    public async Task CancellingAStreamAbortsTheUpstreamRequest()
    {
        var upstream = StubUpstream.Sse(
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{"content":"a"},"finish_reason":null}]}""",
            """data: {"id":"c","created":0,"model":"llama3","choices":[{"index":0,"delta":{"content":"b"},"finish_reason":null}]}""",
            "data: [DONE]");

        using var cts = new CancellationTokenSource();
        var backend = Backend(upstream);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in backend.StreamAsync(
                "chat",
                """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":true}""",
                cts.Token))
            {
                // Walk away after the first chunk, exactly as the coordinator does when the
                // client hangs up.
                await cts.CancelAsync();
            }
        });

        // One abandoned client must not cost one leaked upstream HTTP stream.
        Assert.True(upstream.ResponseStreamDisposed);
    }

    // ---- embeddings ------------------------------------------------------------------

    [Fact]
    public async Task EmbeddingsAskForFloatsAndComeBackOllamaShaped()
    {
        var upstream = StubUpstream.Json("""
        {
          "object": "list",
          "model": "nomic-embed-text",
          "data": [{"object":"embedding","index":0,"embedding":[0.1,0.2,0.3]}],
          "usage": {"prompt_tokens": 6, "total_tokens": 6}
        }
        """);

        var responseJson = await Backend(upstream).EmbedAsync("""
        {"model":"nomic-embed-text","input":"hello"}
        """, CancellationToken.None);

        // We control this call, so we never ask for base64 and never have to decode it.
        Assert.Equal("/v1/embeddings", upstream.LastRequestPath);
        Assert.Equal("float", upstream.LastRequestBody().GetProperty("encoding_format").GetString());

        var ollama = Parse(responseJson);
        var vector = ollama.GetProperty("embeddings")[0];
        Assert.Equal(3, vector.GetArrayLength());
        Assert.Equal(0.1f, vector[0].GetSingle(), 3);
        Assert.Equal(6, ollama.GetProperty("prompt_eval_count").GetInt32());
    }

    // ---- models ----------------------------------------------------------------------

    [Fact]
    public async Task ListModelsReportsNullDigestAndSize()
    {
        var upstream = StubUpstream.Json("""
        {"object":"list","data":[{"id":"meta-llama/Llama-3.1-8B-Instruct","object":"model","created":0,"owned_by":"vllm"}]}
        """);

        var models = await Backend(upstream).ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("meta-llama/Llama-3.1-8B-Instruct", model.Name);
        Assert.Null(model.Digest);
        Assert.Null(model.SizeBytes);
    }

    [Fact]
    public async Task ListModelsAppliesTheAllowlist()
    {
        // The reason the allowlist is effectively mandatory against a hosted provider: without
        // it the node claims a catalogue it cannot serve, and the router believes it.
        var upstream = StubUpstream.Json("""
        {"object":"list","data":[
          {"id":"gpt-4o-mini","object":"model","created":0,"owned_by":"openai"},
          {"id":"gpt-4o","object":"model","created":0,"owned_by":"openai"},
          {"id":"dall-e-3","object":"model","created":0,"owned_by":"openai"}
        ]}
        """);

        var options = new OpenAiBackendOptions
        {
            BaseUrl = "http://upstream/v1",
            Models = new ModelFilterOptions { Include = ["gpt-4o-mini"] }
        };

        var models = await Backend(upstream, options).ListModelsAsync(CancellationToken.None);

        Assert.Equal("gpt-4o-mini", Assert.Single(models).Name);
    }

    [Fact]
    public async Task AnUnreachableUpstreamReportsNoModelsRatherThanCrashingTheNode()
    {
        var upstream = StubUpstream.Status(HttpStatusCode.ServiceUnavailable, "down");

        Assert.Empty(await Backend(upstream).ListModelsAsync(CancellationToken.None));
    }

    // ---- upstream errors -------------------------------------------------------------

    [Fact]
    public async Task AnUpstream401CarriesTheUpstreamsOwnMessage()
    {
        var upstream = StubUpstream.Status(HttpStatusCode.Unauthorized, """
        {"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}
        """);

        var ex = await Assert.ThrowsAsync<OpenAiUpstreamException>(
            () => Backend(upstream).ChatAsync("""{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""", CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("Incorrect API key provided", ex.Message);
    }

    [Fact]
    public async Task AnUpstream500SurfacesAsAFailedJob()
    {
        var upstream = StubUpstream.Status(HttpStatusCode.InternalServerError, "engine crashed");

        var ex = await Assert.ThrowsAsync<OpenAiUpstreamException>(
            () => Backend(upstream).ChatAsync("""{"model":"llama3","messages":[{"role":"user","content":"hi"}]}""", CancellationToken.None));

        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("engine crashed", ex.Message);
    }

    [Fact]
    public void TheBackendReportsItsUpstreamAsItsEndpoint()
    {
        // A node that reported localhost:11434 while driving vLLM would be lying on the status page.
        Assert.Equal("http://upstream/v1", Backend(StubUpstream.Json("{}")).Endpoint);
    }

    // ---- harness ---------------------------------------------------------------------

    private static OpenAiBackend Backend(StubUpstream upstream, OpenAiBackendOptions? options = null)
        => new(
            new StubHttpClientFactory(upstream),
            Options.Create(options ?? new OpenAiBackendOptions { BaseUrl = "http://upstream/v1" }),
            NullLogger<OpenAiBackend>.Instance);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<List<JsonElement>> Collect(IAsyncEnumerable<string> chunks)
    {
        var collected = new List<JsonElement>();

        await foreach (var chunk in chunks)
        {
            collected.Add(Parse(chunk));
        }

        return collected;
    }

    private sealed class StubHttpClientFactory(StubUpstream upstream) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(upstream, disposeHandler: false);
    }

    private sealed class StubUpstream : HttpMessageHandler
    {
        private HttpStatusCode status = HttpStatusCode.OK;
        private string body = "{}";
        private string contentType = "application/json";
        private string? requestBody;
        private TrackingStream? responseStream;

        public string? LastRequestPath { get; private set; }

        public bool ResponseStreamDisposed => responseStream?.Disposed ?? false;

        public static StubUpstream Json(string response)
            => new() { body = response };

        public static StubUpstream Sse(params string[] lines)
            => new()
            {
                body = string.Join("\n", lines) + "\n",
                contentType = "text/event-stream"
            };

        public static StubUpstream Status(HttpStatusCode status, string response)
            => new() { status = status, body = response };

        public JsonElement LastRequestBody()
            => JsonDocument.Parse(requestBody ?? "{}").RootElement.Clone();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestPath = request.RequestUri?.AbsolutePath;

            if (request.Content is not null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            responseStream = new TrackingStream(Encoding.UTF8.GetBytes(body));

            return new HttpResponseMessage(status)
            {
                Content = new StreamContent(responseStream)
                {
                    Headers = { { "Content-Type", contentType } }
                }
            };
        }
    }

    /// <summary>A response body that remembers whether the caller closed it.</summary>
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
