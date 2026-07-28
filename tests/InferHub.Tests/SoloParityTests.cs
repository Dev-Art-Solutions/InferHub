using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// The test this phase exists for: <strong>the same request, to the hub and to a solo node,
/// produces the same thing on the wire.</strong>
/// </summary>
/// <remarks>
/// <para>
/// The promise of solo mode is that moving between deployments is one line — a changed
/// <c>base_url</c>, with no second diff hiding behind it. The failure most likely to ship is not a
/// broken endpoint but a <em>subtly different</em> one: a divergent <c>finish_reason</c>, a missing
/// key, a different status. Neither host's own tests would notice, because each is self-consistent.
/// </para>
/// <para>
/// So both sides run on real Kestrel behind a real <see cref="HttpClient"/> over the same scripted
/// payloads, and responses are compared after normalising only what is <em>allowed</em> to differ:
/// ids and timestamps, which are minted per request on both sides. Calling handlers directly would
/// prove the handlers agree and say nothing about what a client receives — the same reasoning that
/// put <c>NodeHubStreamingTests</c> on a real wire after streaming shipped broken for several
/// releases.
/// </para>
/// </remarks>
public class SoloParityTests
{
    private const string ChatResponse = """
    {"model":"llama3","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"Hello there."},"done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":4}
    """;

    private const string GenerateResponse = """
    {"model":"llama3","created_at":"2026-01-01T00:00:00Z","response":"Hello there.","done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":4}
    """;

    // ---- blocking ---------------------------------------------------------------------------

    [Fact]
    public async Task ChatCompletionsMatch()
    {
        await using var pair = await Pair.StartAsync(ChatResponse);

        await pair.AssertSameJsonAsync(
            "/v1/chat/completions",
            """{"model":"llama3","messages":[{"role":"user","content":"Hi!"}]}""");
    }

    [Fact]
    public async Task LegacyCompletionsMatch()
    {
        await using var pair = await Pair.StartAsync(GenerateResponse);

        await pair.AssertSameJsonAsync("/v1/completions", """{"model":"llama3","prompt":"Hi!"}""");
    }

    [Fact]
    public async Task TheOllamaDialectMatchesToo()
    {
        await using var pair = await Pair.StartAsync(ChatResponse);

        await pair.AssertSameJsonAsync(
            "/api/chat",
            """{"model":"llama3","messages":[{"role":"user","content":"Hi!"}],"stream":false}""");
    }

    [Fact]
    public async Task EmbeddingsMatch()
    {
        await using var pair = await Pair.StartAsync(ChatResponse);

        await pair.AssertSameJsonAsync("/v1/embeddings", """{"model":"nomic","input":"hello"}""");
    }

    // ---- streaming --------------------------------------------------------------------------

    [Fact]
    public async Task StreamingChatFramesMatch()
    {
        await using var pair = await Pair.StartAsync(ChatResponse, ChatStream);

        await pair.AssertSameSseAsync(
            "/v1/chat/completions",
            """{"model":"llama3","messages":[{"role":"user","content":"Hi!"}],"stream":true}""");
    }

    [Fact]
    public async Task StreamingUsageFramesMatchWhenAskedFor()
    {
        await using var pair = await Pair.StartAsync(ChatResponse, ChatStream);

        await pair.AssertSameSseAsync(
            "/v1/chat/completions",
            """
            {"model":"llama3","messages":[{"role":"user","content":"Hi!"}],"stream":true,"stream_options":{"include_usage":true}}
            """);
    }

    [Fact]
    public async Task StreamingLegacyCompletionFramesMatch()
    {
        await using var pair = await Pair.StartAsync(GenerateResponse,
        [
            """{"model":"llama3","response":"He","done":false}""",
            """{"model":"llama3","response":"llo","done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":4}"""
        ]);

        await pair.AssertSameSseAsync("/v1/completions", """{"model":"llama3","prompt":"Hi!","stream":true}""");
    }

    [Fact]
    public async Task StreamingOllamaNdjsonMatchesByteForByte()
    {
        await using var pair = await Pair.StartAsync(ChatResponse, ChatStream);

        var (hub, node) = await pair.PostRawAsync(
            "/api/chat",
            """{"model":"llama3","messages":[{"role":"user","content":"Hi!"}],"stream":true}""");

        // Nothing in an NDJSON frame is minted per request, so this one really is byte for byte.
        Assert.Equal(hub.Body, node.Body);
        Assert.Equal(hub.ContentType, node.ContentType);
    }

    // ---- the features that must survive the trip ----------------------------------------------

    [Fact]
    public async Task StreamingToolCallsResolveTheSameFinishReason()
    {
        // Phase 27's live-found shape: Ollama streams the tool call on a NON-terminal chunk, and
        // the terminal frame must still resolve finish_reason=tool_calls. That memory lives in
        // ChatStreamFormatter, which both hosts now share — this is what proves they share it.
        await using var pair = await Pair.StartAsync(ChatResponse,
        [
            """{"model":"llama3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_weather","arguments":{"city":"Sofia"}}}]},"done":false}""",
            """{"model":"llama3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}"""
        ]);

        var body = """
        {"model":"llama3","messages":[{"role":"user","content":"weather?"}],"stream":true,"tools":[{"type":"function","function":{"name":"get_weather","parameters":{}}}]}
        """;

        await pair.AssertSameSseAsync("/v1/chat/completions", body);

        var (_, node) = await pair.PostRawAsync("/v1/chat/completions", body);
        Assert.Contains("tool_calls", node.Body);
    }

    [Fact]
    public async Task AVisionRequestTranslatesIdentically()
    {
        // A 1x1 PNG. Phase 29 splits text and images into Ollama's two fields and sniffs the media
        // type from the magic bytes; both live in the shared translator.
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        await using var pair = await Pair.StartAsync(ChatResponse);

        var body = """
        {"model":"llava","messages":[{"role":"user","content":[{"type":"text","text":"what is this?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,PNG"}}]}]}
        """.Replace("PNG", png);

        await pair.AssertSameJsonAsync("/v1/chat/completions", body);

        // And the same Ollama-shaped body reached the backend on both sides: the images array,
        // not a joined string.
        Assert.NotNull(pair.Node.Backend.LastRequestJson);
        Assert.Contains("\"images\"", pair.Node.Backend.LastRequestJson);
        Assert.Equal(pair.Hub.LastJobJson, pair.Node.Backend.LastRequestJson);
    }

    [Fact]
    public async Task AnUpstreamRefusalIsUnwrappedTheSameWay()
    {
        // Phase-29 D6's real captured payload: Ollama encodes its backend's JSON error as a string
        // inside its own error field, so it arrives double-encoded.
        const string raw = """{"error":"{\"error\":{\"code\":400,\"message\":\"this model does not support multimodal requests\"}}"}""";

        await using var pair = await Pair.StartAsync(ChatResponse, failure: raw);

        var (hub, node) = await pair.PostJsonAsync(
            "/v1/chat/completions",
            """{"model":"llama3","messages":[{"role":"user","content":"Hi!"}]}""");

        AssertSameJson(hub, node);
        Assert.Equal(
            "this model does not support multimodal requests",
            node.Json.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheModelListLooksTheSameOnBoth()
    {
        await using var pair = await Pair.StartAsync(ChatResponse);

        var (hub, node) = await pair.GetJsonAsync("/v1/models");
        AssertSameJson(hub, node);
    }

    [Fact]
    public async Task AnUnknownModelLooksTheSameOnBoth()
    {
        await using var pair = await Pair.StartAsync(ChatResponse);

        var (hub, node) = await pair.GetJsonAsync("/v1/models/not-a-real-model");

        AssertSameJson(hub, node);
        Assert.Equal(HttpStatusCode.NotFound, node.Status);
    }

    [Fact]
    public async Task TheOllamaTagListLooksTheSameOnBoth()
    {
        await using var pair = await Pair.StartAsync(ChatResponse);

        var (hub, node) = await pair.GetJsonAsync("/api/tags");
        AssertSameJson(hub, node);
    }

    // ---- the guard on the guard ----------------------------------------------------------------

    [Fact]
    public async Task TheComparisonActuallyDetectsADifference()
    {
        // A parity assertion that cannot fail is decoration. Feed the two hosts deliberately
        // different output and confirm the comparison notices — including through the normaliser,
        // which strips ids and could just as easily strip everything.
        await using var pair = await Pair.StartAsync(ChatResponse);
        pair.Node.Backend.BlockingResponse = ChatResponse.Replace("Hello there.", "Something else.");

        var (hub, node) = await pair.PostJsonAsync(
            "/v1/chat/completions",
            """{"model":"llama3","messages":[{"role":"user","content":"Hi!"}]}""");

        Assert.ThrowsAny<Exception>(() => AssertSameJson(hub, node));
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static readonly string[] ChatStream =
    [
        """{"model":"llama3","message":{"role":"assistant","content":"He"},"done":false}""",
        """{"model":"llama3","message":{"role":"assistant","content":"llo"},"done":false}""",
        """{"model":"llama3","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":4}"""
    ];

    private sealed class Pair : IAsyncDisposable
    {
        public HubHost Hub { get; private set; } = null!;

        public SoloHost Node { get; private set; } = null!;

        public static async Task<Pair> StartAsync(
            string blockingResponse,
            string[]? streamChunks = null,
            string? failure = null)
        {
            var backend = new ScriptedBackend
            {
                BlockingResponse = blockingResponse,
                Failure = failure is null ? null : new InvalidOperationException(failure)
            };

            if (streamChunks is not null)
            {
                backend.Streaming(streamChunks);
            }

            return new Pair
            {
                Hub = await HubHost.StartAsync(blockingResponse, streamChunks, failure),
                Node = await SoloHost.StartAsync(backend)
            };
        }

        public async Task<(Response Hub, Response Node)> PostJsonAsync(string path, string body)
            => (await SendJsonAsync(Hub.Client, HttpMethod.Post, path, body),
                await SendJsonAsync(Node.Client, HttpMethod.Post, path, body));

        public async Task<(Response Hub, Response Node)> GetJsonAsync(string path)
            => (await SendJsonAsync(Hub.Client, HttpMethod.Get, path, null),
                await SendJsonAsync(Node.Client, HttpMethod.Get, path, null));

        public async Task<(RawResponse Hub, RawResponse Node)> PostRawAsync(string path, string body)
            => (await SendRawAsync(Hub.Client, path, body),
                await SendRawAsync(Node.Client, path, body));

        public async Task AssertSameJsonAsync(string path, string body)
        {
            var (hub, node) = await PostJsonAsync(path, body);
            AssertSameJson(hub, node);
        }

        public async Task AssertSameSseAsync(string path, string body)
        {
            var (hub, node) = await PostRawAsync(path, body);

            Assert.Equal(hub.ContentType, node.ContentType);
            Assert.Equal(NormaliseSse(hub.Body), NormaliseSse(node.Body));
        }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            await Node.DisposeAsync();
        }
    }

    private static async Task<Response> SendJsonAsync(HttpClient client, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return new Response(
            response.StatusCode,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone());
    }

    private static async Task<RawResponse> SendRawAsync(HttpClient client, string path, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        return new RawResponse(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    private static void AssertSameJson(Response hub, Response node)
    {
        Assert.Equal(hub.Status, node.Status);
        Assert.Equal(Normalise(hub.Json), Normalise(node.Json));
    }

    /// <summary>
    /// Strips only what a response is <em>allowed</em> to differ on: the completion id and the
    /// creation timestamp, both minted per request on both sides. Everything else is contract.
    /// </summary>
    private static string Normalise(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        Strip(node);
        return node?.ToJsonString() ?? "null";

        static void Strip(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    obj.Remove("id");
                    obj.Remove("created");
                    obj.Remove("created_at");

                    foreach (var child in obj.ToList())
                    {
                        Strip(child.Value);
                    }

                    break;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        Strip(item);
                    }

                    break;
            }
        }
    }

    private static string NormaliseSse(string body)
    {
        const string prefix = "data: ";

        var frames = body
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame => frame.Trim())
            .Where(frame => frame.Length > 0)
            .Select(frame =>
            {
                var payload = frame.StartsWith(prefix) ? frame[prefix.Length..] : frame;

                if (payload == "[DONE]")
                {
                    return payload;
                }

                using var document = JsonDocument.Parse(payload);
                return Normalise(document.RootElement);
            });

        return string.Join("\n", frames);
    }

    private sealed record Response(HttpStatusCode Status, JsonElement Json);

    private sealed record RawResponse(HttpStatusCode Status, string Body, string? ContentType);
}
