using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// Phase-38 D10. <strong>The same corpus and the same question, to the hub and to a solo node,
/// ground the answer the same way.</strong>
/// </summary>
/// <remarks>
/// <para>
/// Phase 38 moved the retrieval pipeline itself into <c>InferHub.Shared</c>, so fusion, k clamping,
/// the context template and the citation shape are one definition and cannot drift. What is
/// <em>not</em> shared, and is therefore what this suite is actually guarding, is the layer around
/// it: each host parses the <c>X-InferHub-Retrieve*</c> headers itself, serialises
/// <c>X-InferHub-Sources</c> itself, and decides its own statuses. Those are the things a client
/// sees, and the promise of solo mode is that it does not have to know which host answered.
/// </para>
/// <para>
/// Both sides run on real Kestrel behind a real <see cref="HttpClient"/>, over the same documents
/// and the same deterministic embedder (<see cref="TestEmbeddings"/>), and the assertions compare
/// what a client receives — plus the augmented body the backend was actually handed, because "the
/// request succeeded" is not the same claim as "the context got there".
/// </para>
/// </remarks>
public class SoloRetrievalParityTests
{
    private const string Question = "how much annual leave do I get?";

    private static readonly (string Id, string Text)[] Corpus =
    [
        ("leave-policy", "Employees accrue 25 days of annual leave each year, plus public holidays."),
        ("kitchen", "The espresso machine is descaled every second Friday by the office manager."),
        ("expenses", "Expense claims over 200 EUR need a receipt and a line manager's approval.")
    ];

    [Fact]
    public async Task TheAugmentedPromptIsIdentical()
    {
        await using var pair = await Pair.StartAsync();

        var (hub, node) = await pair.AskAsync(Question);

        // The system message the model is about to read — the whole point of retrieval. If these
        // two ever differ, "change one base_url" has quietly become false.
        Assert.Equal(SystemPrompt(hub.Body), SystemPrompt(node.Body));
    }

    [Fact]
    public async Task TheSourcesHeaderIsIdentical()
    {
        await using var pair = await Pair.StartAsync();

        var (hub, node) = await pair.AskAsync(Question);

        Assert.NotNull(hub.Sources);
        Assert.Equal(hub.Sources, node.Sources);

        // And it is a citation, not a bare id: a chunk id alone tells the reader nothing about
        // where the answer came from (phase-23, X-InferHub-Sources v2.5).
        Assert.Contains("leave-policy", hub.Sources);
    }

    [Theory]
    [InlineData("vector")]
    [InlineData("keyword")]
    [InlineData("hybrid")]
    public async Task TheSearchRankingIsIdenticalInEveryMode(string mode)
    {
        await using var pair = await Pair.StartAsync();

        var hub = await pair.SearchAsync(pair.Hub.Client, mode);
        var node = await pair.SearchAsync(pair.Node.Client, mode);

        Assert.Equal(hub, node);

        // A ranking that is identical because both sides returned nothing would pass silently.
        Assert.NotEmpty(hub);
    }

    [Fact]
    public async Task TheGenerateDialectAugmentsIdenticallyToo()
    {
        await using var pair = await Pair.StartAsync();

        var hub = await pair.SendAsync(pair.Hub.Client, GenerateRequest());
        var node = await pair.SendAsync(pair.Node.Client, GenerateRequest());

        // /api/generate prepends the context block to the prompt rather than inserting a system
        // message, so this is a genuinely different injection path from the chat one.
        Assert.Equal(Prompt(pair.Hub.LastJobJson!), Prompt(pair.Node.Backend.LastRequestJson!));
        Assert.Equal(hub.Sources, node.Sources);
    }

    [Fact]
    public async Task AnUnknownModeIsTheSameRefusalOnBothHosts()
    {
        await using var pair = await Pair.StartAsync();

        var request = () =>
        {
            var message = ChatRequest();
            message.Headers.Add("X-InferHub-Retrieve-Mode", "magic");
            return message;
        };

        var hub = await pair.Hub.Client.SendAsync(request());
        var node = await pair.Node.Client.SendAsync(request());

        Assert.Equal(hub.StatusCode, node.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, node.StatusCode);
    }

    [Fact]
    public async Task TheComparisonActuallyDetectsADifference()
    {
        // Guard the guard, as phase-37 D7 does. Ask the two hosts different questions and the
        // augmented prompts must diverge — a parity suite that cannot fail is decoration.
        //
        // k=1 on both sides, deliberately: with the default k the whole three-document corpus fits
        // in every context block and two different questions would produce the same text, so the
        // assertion would pass without proving anything. It has to compare a *choice*.
        await using var pair = await Pair.StartAsync();

        await pair.SendAsync(pair.Hub.Client, TopOne(Question));
        var hub = pair.Hub.LastJobJson!;

        await pair.SendAsync(pair.Node.Client, TopOne("when is the espresso machine descaled?"));
        var node = pair.Node.Backend.LastRequestJson!;

        Assert.NotEqual(SystemPrompt(hub), SystemPrompt(node));
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>The injected system message, which is where the retrieved context lands.</summary>
    private static string SystemPrompt(string ollamaJson)
    {
        using var doc = JsonDocument.Parse(ollamaJson);
        return doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system")
            .Select(m => m.GetProperty("content").GetString() ?? string.Empty)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("no system message was injected");
    }

    private static string Prompt(string ollamaJson)
    {
        using var doc = JsonDocument.Parse(ollamaJson);
        return doc.RootElement.GetProperty("prompt").GetString() ?? string.Empty;
    }

    private static HttpRequestMessage ChatRequest(string question = Question)
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
        request.Headers.Add("X-InferHub-Retrieve", "handbook");
        return request;
    }

    private static HttpRequestMessage TopOne(string question)
    {
        var request = ChatRequest(question);
        request.Headers.Add("X-InferHub-Retrieve-K", "1");
        return request;
    }

    private static HttpRequestMessage GenerateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = Json(JsonSerializer.Serialize(new { model = "llama3", prompt = Question, stream = false }))
        };
        request.Headers.Add("X-InferHub-Retrieve", "handbook");
        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed record Answer(string Body, string? Sources);

    private sealed class Pair : IAsyncDisposable
    {
        public HubHost Hub { get; private init; } = null!;

        public SoloHost Node { get; private init; } = null!;

        public static async Task<Pair> StartAsync()
        {
            var pair = new Pair
            {
                Hub = await HubHost.StartAsync(ChatResponse, retrieval: true),
                Node = await SoloRetrievalTests.StartWithRetrievalAsync()
            };

            // The hub does not auto-provision for an unscoped client (phase-23 / phase-31 D5), so
            // its collection is created explicitly; the node's first ingest creates its own. Same
            // dimension either way, because the same embedder measures it.
            await pair.Hub.CreateCollectionAsync("handbook", TestEmbeddings.Dimension);

            foreach (var (id, text) in Corpus)
            {
                await Ingest(pair.Hub.Client, id, text);
                await Ingest(pair.Node.Client, id, text);
            }

            return pair;
        }

        public async Task<(Answer Hub, Answer Node)> AskAsync(string question)
        {
            var hub = await SendAsync(Hub.Client, ChatRequest(question));
            var node = await SendAsync(Node.Client, ChatRequest(question));

            return (hub with { Body = Hub.LastJobJson! }, node with { Body = Node.Backend.LastRequestJson! });
        }

        public async Task<Answer> SendAsync(HttpClient client, HttpRequestMessage request)
        {
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var sources = response.Headers.TryGetValues("X-InferHub-Sources", out var values)
                ? values.Single()
                : null;

            return new Answer(await response.Content.ReadAsStringAsync(), sources);
        }

        /// <summary>The ranked ids for a mode, which is what a corpus owner actually compares.</summary>
        public async Task<string[]> SearchAsync(HttpClient client, string mode)
        {
            var response = await client.PostAsync(
                "/api/collections/handbook/search",
                Json(JsonSerializer.Serialize(new { query = Question, mode })));

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return [.. body.GetProperty("hits").EnumerateArray()
                .Select(hit => hit.GetProperty("id").GetString() ?? string.Empty)];
        }

        private static async Task Ingest(HttpClient client, string id, string text)
        {
            var response = await client.PostAsync(
                "/api/collections/handbook/documents",
                Json(JsonSerializer.Serialize(new { id, text })));

            response.EnsureSuccessStatusCode();
        }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            await Node.DisposeAsync();
        }
    }

    private const string ChatResponse = """
    {"model":"llama3","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"Hello there."},"done":true,"done_reason":"stop","prompt_eval_count":11,"eval_count":4}
    """;
}
