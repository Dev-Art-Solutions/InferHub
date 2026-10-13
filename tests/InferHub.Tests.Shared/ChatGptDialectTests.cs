using System.Net;
using System.Text.Json;
using InferHub.Shared.ChatGpt;

namespace InferHub.Tests;

/// <summary>
/// Phase 103. The Responses API dialect a ChatGPT-plan request speaks, against <see cref="FakeOpenAi"/>:
/// what is sent (<c>store: false</c>, <c>stream: true</c>, no refused field), how the stream becomes
/// Ollama JSON, and how each documented failure keeps its own status and code.
/// </summary>
public sealed class ChatGptDialectTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-chatgpt-" + Guid.NewGuid().ToString("N"));
    private readonly FakeOpenAi openAi = new();
    private ChatGptSession session = null!;

    private const string Chat = """
        {"model":"gpt-6.1-sol","messages":[{"role":"system","content":"Be terse."},
         {"role":"user","content":"hi"},{"role":"assistant","content":"Hi."},{"role":"user","content":"more"}],
         "options":{"temperature":0.2,"top_p":0.9,"num_predict":64,"seed":7}}
        """;

    public async Task InitializeAsync()
    {
        session = new ChatGptSession(new ChatGptCredentialStore(directory), openAi.Client, new IdTokenValidator(_ => Task.FromResult(openAi.Jwks())));

        var signIn = ChatGptBrowserSignIn.Begin(session.HostId(), session.Hints(null, false), 0);
        var grant = signIn.CompleteAsync(openAi.Client(), session.Validator, null, TimeSpan.FromSeconds(20), CancellationToken.None);
        using (var browser = new HttpClient())
        {
            await browser.GetStringAsync(openAi.Authorize(signIn.AuthorizeUrl));
        }

        await session.ApplySignInAsync(await grant, CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        openAi.Dispose();

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheBodyIsStoreFalseStreamTrueWithInstructionsAndNoRefusedField()
    {
        string? sent = null;
        openAi.Responses = body => { sent = body; return (HttpStatusCode.OK, FakeOpenAi.Completed("ok"), null); };

        await Client().ChatAsync(Chat, CancellationToken.None);

        using var document = JsonDocument.Parse(sent!);
        var root = document.RootElement;

        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("gpt-6.1-sol", root.GetProperty("model").GetString());

        // A system turn is lifted to instructions: an explicit system item is rejected by the vendor.
        Assert.Equal("Be terse.", root.GetProperty("instructions").GetString());
        var input = root.GetProperty("input");
        Assert.Equal(3, input.GetArrayLength());
        Assert.Equal(["user", "assistant", "user"], input.EnumerateArray().Select(i => i.GetProperty("role").GetString()!).ToArray());

        // 103 D4: every field the plan-usage route refuses stays out, whatever Ollama options said.
        foreach (var refused in new[] { "temperature", "top_p", "max_output_tokens", "seed", "user", "metadata", "previous_response_id" })
        {
            Assert.False(root.TryGetProperty(refused, out _), $"'{refused}' must not be sent");
        }
    }

    [Fact]
    public async Task TheTokenIsTheSessionsBearerAndNeverAnApiKey()
    {
        await Client().ChatAsync(Chat, CancellationToken.None);

        var call = openAi.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal));
        Assert.Equal("https://api.openai.com/v1/responses", call.Uri.ToString());
        Assert.StartsWith("Bearer at-", call.Authorization);
    }

    [Fact]
    public async Task AStreamBecomesOllamaChunksEndingInADoneChunkWithTheUsage()
    {
        openAi.Responses = _ => (HttpStatusCode.OK, FakeOpenAi.Completed("Hel", "lo"), null);

        var chunks = new List<JsonElement>();
        await foreach (var chunk in Client().StreamAsync("chat", Chat, CancellationToken.None))
        {
            chunks.Add(JsonDocument.Parse(chunk).RootElement);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hel", chunks[0].GetProperty("message").GetProperty("content").GetString());
        Assert.False(chunks[0].GetProperty("done").GetBoolean());

        var done = chunks[^1];
        Assert.True(done.GetProperty("done").GetBoolean());
        Assert.Equal("stop", done.GetProperty("done_reason").GetString());
        Assert.Equal(12, done.GetProperty("prompt_eval_count").GetInt32());
        Assert.Equal(3, done.GetProperty("eval_count").GetInt32());
    }

    [Fact]
    public async Task ABlockingCallIsTheStreamFoldedIntoOneAnswer()
    {
        openAi.Responses = _ => (HttpStatusCode.OK, FakeOpenAi.Completed("Hello", ", world"), null);

        using var answer = JsonDocument.Parse(await Client().GenerateAsync("""{"model":"gpt-6.1-sol","prompt":"hi","system":"terse"}""", CancellationToken.None));

        Assert.Equal("Hello, world", answer.RootElement.GetProperty("response").GetString());
        Assert.True(answer.RootElement.GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task AStreamThatEndsWithoutCompletedIsAFailureNotAShortAnswer()
    {
        openAi.Responses = _ => (HttpStatusCode.OK, [JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = "Hal" })], null);

        var ex = await Assert.ThrowsAsync<ChatGptUpstreamException>(() => Client().ChatAsync(Chat, CancellationToken.None));
        Assert.Contains("response.completed", ex.Message);
    }

    [Fact]
    public async Task AUsageLimitMidStreamIsA429WithItsCodeAndPausesTheAccount()
    {
        openAi.Responses = _ => (HttpStatusCode.OK,
        [
            JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = "Par" }),
            """{"type":"response.failed","response":{"status":"failed","error":{"code":"subscription_sharing_usage_limit_exceeded","message":"Usage limit reached."}}}"""
        ], null);

        var ex = await Assert.ThrowsAsync<ChatGptUpstreamException>(async () =>
        {
            await foreach (var _ in Client().StreamAsync("chat", Chat, CancellationToken.None))
            {
            }
        });

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("subscription_sharing_usage_limit_exceeded", ex.Code);
        Assert.Contains("ChatGPT Settings → Usage", ex.Message);

        // The vendor: pause new requests. The next one is refused here, without a call.
        var before = openAi.Requests.Count;
        var paused = await Assert.ThrowsAsync<ChatGptUnavailableException>(() => Client().ChatAsync(Chat, CancellationToken.None));
        Assert.Equal(429, paused.StatusCode);
        Assert.Equal(before, openAi.Requests.Count);
        Assert.NotNull(session.State().UsagePausedUntilUtc);
    }

    [Theory]
    [InlineData(403, """{"error":{"code":"subscription_sharing_user_not_eligible","message":"Not eligible."}}""", "subscription_sharing_user_not_eligible")]
    [InlineData(400, """{"error":{"code":"subscription_sharing_unsupported_capability","message":"Unsupported.","param":"tools"}}""", "subscription_sharing_unsupported_capability")]
    [InlineData(403, """{"detail":"Region not supported."}""", null)]
    [InlineData(503, """{"detail":"Direct routing unavailable."}""", null)]
    public async Task ARefusalBeforeTheStreamKeepsItsStatusCodeAndRequestId(int status, string body, string? code)
    {
        openAi.Responses = _ => ((HttpStatusCode)status, [], body);

        var ex = await Assert.ThrowsAsync<ChatGptUpstreamException>(() => Client().ChatAsync(Chat, CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(code, ex.Code);
        Assert.Equal("req_test", ex.RequestId);
        Assert.StartsWith("ChatGPT: ", ex.Message);
    }

    [Fact]
    public async Task A401IsOneRefreshAndOneRetryAndThenAnAnswer()
    {
        openAi.UnauthorizedResponses = 1;

        using var answer = JsonDocument.Parse(await Client().ChatAsync(Chat, CancellationToken.None));

        Assert.Equal(1, openAi.Refreshes);
        Assert.Equal("Hello world", answer.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ARequestThatDeclaresToolsIsRefusedWithoutBeingSent()
    {
        var before = openAi.Requests.Count;

        var ex = await Assert.ThrowsAsync<ChatGptUpstreamException>(() => Client().ChatAsync(
            """{"model":"gpt-6.1-sol","messages":[{"role":"user","content":"weather?"}],"tools":[{"type":"function","function":{"name":"w"}}]}""",
            CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(before, openAi.Requests.Count);
    }

    [Fact]
    public async Task AnImageTravelsAsAnInputImageDataUrl()
    {
        string? sent = null;
        openAi.Responses = body => { sent = body; return (HttpStatusCode.OK, FakeOpenAi.Completed("a pixel"), null); };
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        await Client().ChatAsync($$"""{"model":"gpt-6.1-sol","messages":[{"role":"user","content":"what?","images":["{{png}}"]}]}""", CancellationToken.None);

        using var document = JsonDocument.Parse(sent!);
        var parts = document.RootElement.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("input_text", parts[0].GetProperty("type").GetString());
        Assert.Equal("input_image", parts[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetString());
    }

    [Fact]
    public async Task TheModelListIsTheAccountsVisibleCatalogueInOrder()
    {
        var ids = await Client().ListModelIdsAsync(CancellationToken.None);

        Assert.Equal(["gpt-6.1-sol", "gpt-6.1-mini"], ids);
    }

    [Fact]
    public async Task EmbeddingsAreNotPartOfPlanUsage()
    {
        var ex = await Assert.ThrowsAsync<ChatGptUpstreamException>(() => Client().EmbedAsync("""{"model":"x","input":"a"}""", CancellationToken.None));
        Assert.Equal(501, ex.StatusCode);
    }

    private ChatGptResponsesClient Client()
        => new(ChatGptResponsesClient.Configure(openAi.Client(), ChatGptOAuth.Resource, 30), session);
}
