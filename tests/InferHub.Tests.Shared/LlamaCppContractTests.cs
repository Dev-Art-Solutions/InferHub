using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.LlamaCpp;
using InferHub.Shared.OpenAi;

namespace InferHub.Tests;

/// <summary>
/// Phase 96: what both hosts decide the same way about llama.cpp — the native routes' edge (D4), the
/// rerank contract (D5) and the samplers a llama.cpp upstream is sent (D6).
/// </summary>
public class LlamaCppContractTests
{
    // ---- D6: samplers ------------------------------------------------------------------------

    private const string OllamaChat = """
    {"model":"qwen","messages":[{"role":"user","content":"hi"}],
     "options":{"temperature":0.2,"top_k":20,"min_p":0.05,"repeat_penalty":1.1,"mirostat":2,"num_keep":4,"typical_p":"x"}}
    """;

    [Fact]
    public async Task ALlamaCppUpstreamIsSentOllamasSamplersUnderItsOwnNames()
    {
        var upstream = new Recording();
        var client = new OpenAiUpstreamClient(upstream.Client(), llamaCppSamplers: true);

        await client.ChatAsync(OllamaChat, CancellationToken.None);

        using var body = JsonDocument.Parse(upstream.Body!);
        var root = body.RootElement;
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(20, root.GetProperty("top_k").GetInt32());
        Assert.Equal(0.05, root.GetProperty("min_p").GetDouble());
        Assert.Equal(1.1, root.GetProperty("repeat_penalty").GetDouble());
        Assert.Equal(2, root.GetProperty("mirostat").GetInt32());
        Assert.Equal(4, root.GetProperty("n_keep").GetInt32());

        // Not a number: not a sampler setting, so not sent.
        Assert.False(root.TryGetProperty("typical_p", out _));
    }

    [Fact]
    public async Task AnyOtherUpstreamIsSentNoneOfThem()
    {
        var upstream = new Recording();
        var client = new OpenAiUpstreamClient(upstream.Client());

        await client.ChatAsync(OllamaChat, CancellationToken.None);

        using var body = JsonDocument.Parse(upstream.Body!);
        Assert.False(body.RootElement.TryGetProperty("top_k", out _));
        Assert.False(body.RootElement.TryGetProperty("min_p", out _));
    }

    [Fact]
    public async Task ACompletionCarriesThemToo()
    {
        var upstream = new Recording(completion: true);
        var client = new OpenAiUpstreamClient(upstream.Client(), llamaCppSamplers: true);

        await client.GenerateAsync("""{"model":"qwen","prompt":"1,2,","options":{"top_k":5}}""", CancellationToken.None);

        using var body = JsonDocument.Parse(upstream.Body!);
        Assert.Equal(5, body.RootElement.GetProperty("top_k").GetInt32());
    }

    // ---- D4: the native routes' edge ---------------------------------------------------------

    [Theory]
    [InlineData("""{"model":"qwen","prompt":"x"}""", "qwen", "")]
    [InlineData("""{"prompt":"x"}""", null, "model is required")]
    [InlineData("""{"model":"qwen","stream":true}""", null, "/v1/completions")]
    [InlineData("""[1,2]""", null, "JSON object")]
    [InlineData("""not json""", null, "JSON object")]
    public void TheEdgeReadsTheModelAndRefusesAStream(string body, string? model, string error)
    {
        Assert.Equal(model, LlamaCppNative.ModelOf(body, out var refusal));
        Assert.Contains(error, refusal);
    }

    [Fact]
    public void ThePayloadCarriesTheCallersBodyAndOnlyAnAllowedOperation()
    {
        var payload = LlamaCppNative.Payload("infill", """{"model":"q","input_prefix":"def f("}""");
        var (operation, body) = LlamaCppNative.ReadPayload(payload)!.Value;

        Assert.Equal("infill", operation);
        Assert.Equal("""{"model":"q","input_prefix":"def f("}""", body);

        Assert.Null(LlamaCppNative.ReadPayload("""{"operation":"slots"}"""));
        Assert.Null(LlamaCppNative.ReadPayload("""{"operation":"../metrics"}"""));
        Assert.False(LlamaCppNative.IsPost("slots"));
        Assert.False(LlamaCppNative.IsPost("lora-adapters"));
        Assert.True(LlamaCppNative.IsGet("props"));
    }

    [Fact]
    public void WhatGeneratedIsCountedFromTheEnginesOwnFields()
    {
        Assert.Equal((15L, 8L), LlamaCppNative.Tokens("""{"content":"x","tokens_evaluated":15,"tokens_predicted":8}"""));
        Assert.Equal((0L, 0L), LlamaCppNative.Tokens("""{"tokens":[1,2]}"""));
        Assert.True(LlamaCppNative.Generates("completion"));
        Assert.False(LlamaCppNative.Generates("tokenize"));
    }

    [Fact]
    public void TheNodeStatesTheFailureAndTheEdgeRendersIt()
    {
        var id = Guid.NewGuid();

        Assert.Equal(404, LlamaCppNative.Render(ToolResult.Refused(id, "model 'x' not found", BrioErrorCodes.ModelNotFound)).Status);
        Assert.Equal(400, LlamaCppNative.Render(ToolResult.Refused(id, "bad", ToolErrorCodes.InvalidRequest)).Status);
        Assert.Equal(503, LlamaCppNative.Render(ToolResult.Retry(id, "Loading model", 2)).Status);
        Assert.Equal(502, LlamaCppNative.Render(ToolResult.Failed(id, "boom")).Status);

        var ok = LlamaCppNative.Render(ToolResult.Succeeded(id, """{"tokens":[1]}"""));
        Assert.Equal(200, ok.Status);
        Assert.Equal("""{"tokens":[1]}""", ok.Json);
    }

    // ---- D5: rerank --------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"query":"q","documents":["a"]}""", "model is required")]
    [InlineData("""{"model":"m","documents":["a"]}""", "query is required")]
    [InlineData("""{"model":"m","query":"q","documents":[]}""", "non-empty array")]
    [InlineData("""{"model":"m","query":"q","documents":[1]}""", "string or an object")]
    [InlineData("""{"model":"m","query":"q","documents":["a"],"top_n":0}""", "top_n")]
    public void ARerankRequestThatCannotBeAnsweredIsRefusedAtTheEdge(string body, string error)
    {
        Assert.Null(RerankRequest.TryParse(body, out var refusal));
        Assert.Contains(error, refusal);
    }

    [Fact]
    public void CohereAndJinaDocumentShapesAreTheSameDocuments()
    {
        var request = RerankRequest.TryParse("""{"model":"bge","query":"cat","documents":["a cat",{"text":"stocks"}]}""", out _)!;

        Assert.Equal(["a cat", "stocks"], request.Documents);
        Assert.Equal("""{"query":"cat","documents":["a cat","stocks"]}""", request.JobPayload());
    }

    [Fact]
    public void ScoresBecomeResultsBestFirstCutAtTopN()
    {
        var request = RerankRequest.TryParse(
            """{"model":"bge","query":"cat","documents":["a","b","c"],"top_n":2,"return_documents":true}""", out _)!;

        var rendered = request.Render("""{"scores":[0.1,0.9,0.5],"total_tokens":12}""");

        using var document = JsonDocument.Parse(rendered!);
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal([1, 2], results.Select(r => r.GetProperty("index").GetInt32()).ToArray());
        Assert.Equal("b", results[0].GetProperty("document").GetProperty("text").GetString());
        Assert.Equal(12, document.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64());
        Assert.Equal("bge", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void AnAnswerThatIsNotOneScorePerDocumentIsNotRendered()
    {
        var request = RerankRequest.TryParse("""{"model":"bge","query":"cat","documents":["a","b"]}""", out _)!;

        Assert.Null(request.Render("""{"scores":[0.1]}"""));
        Assert.Null(request.Render("""{"scores":[0.1,"x"]}"""));
        Assert.Null(request.Render("""{"nope":1}"""));
    }

    private sealed class Recording(bool completion = false) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        public HttpClient Client() => new(this) { BaseAddress = new Uri("http://127.0.0.1:8080/v1/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);

            var answer = completion
                ? """{"id":"c","object":"text_completion","created":1,"model":"qwen","choices":[{"index":0,"text":"3","finish_reason":"stop"}]}"""
                : """{"id":"c","object":"chat.completion","created":1,"model":"qwen","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
