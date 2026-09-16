using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Vector;

namespace InferHub.Tests;

/// <summary>
/// Phase 75: <c>POST /api/retrieve/federated</c> fans out to several collections and fuses the
/// results. Every case here is the single-collection path (44 D5, 31 D3) run through the fan-out
/// unchanged — the phase adds only the parallelism, the per-source status and the fusion.
/// </summary>
public class FederatedRetrievalTests
{
    private const string ChatResponse =
        """{"model":"llama3","message":{"role":"assistant","content":"ok"},"done":true}""";

    [Fact]
    public async Task FansOutAcrossAHubOwnedAndANodeOwnedCollectionAndFusesBoth()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);

        await hub.CreateCollectionAsync("hub-docs", TestEmbeddings.Dimension);
        await hub.UpsertAsync("hub-docs", new VectorUpsert("h1", Vector: TestEmbeddings.Of("how node ownership works")));

        hub.Ownership.Assign("node-1", ["node-docs"]);
        hub.Dispatcher.CorpusResponse = JsonSerializer.Serialize(new
        {
            matches = new[]
            {
                new
                {
                    id = "n1",
                    score = 0.9,
                    payload = JsonDocument.Parse("""{"text":"node-owned answer"}""").RootElement,
                    metadata = new Dictionary<string, string> { ["documentId"] = "d1" }
                }
            }
        });

        var response = await hub.Client.PostAsync(
            "/api/retrieve/federated",
            JsonBody(new { collections = new[] { "hub-docs", "node-docs" }, query = "node ownership", k = 5 }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var sources = body.GetProperty("sources").EnumerateArray()
            .ToDictionary(s => s.GetProperty("collection").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal("ok", sources["hub-docs"]);
        Assert.Equal("ok", sources["node-docs"]);

        var matchedCollections = body.GetProperty("matches").EnumerateArray()
            .Select(m => m.GetProperty("collection").GetString())
            .ToHashSet();
        Assert.Contains("hub-docs", matchedCollections);
        Assert.Contains("node-docs", matchedCollections);

        // The node-owned name went through the corpus dispatcher, exactly as /search does it.
        Assert.Equal(InferHub.Shared.Contracts.CorpusJobKinds.Search, hub.Dispatcher.LastJobKind);
    }

    [Fact]
    public async Task ACollectionOutsideTheCallersScopeIsReportedNotFoundNotForbidden()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);
        await hub.CreateCollectionAsync("secret-docs", TestEmbeddings.Dimension);

        // No API keys are configured in this fixture, so every caller is the unscoped default and
        // CanAccess would say yes — this test instead pins the genuinely-missing case, which the
        // fan-out must report identically to an out-of-scope one (31 D3, applied per name in 75 D3).
        var response = await hub.Client.PostAsync(
            "/api/retrieve/federated",
            JsonBody(new { collections = new[] { "secret-docs", "does-not-exist" }, query = "anything" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var sources = body.GetProperty("sources").EnumerateArray()
            .ToDictionary(s => s.GetProperty("collection").GetString()!, s => s.GetProperty("status").GetString());

        Assert.Equal("not_found", sources["does-not-exist"]);
    }

    [Fact]
    public async Task ADisconnectedOwnerIsReportedUnavailableAndNeverAnsweredFromTheHubsOwnStore()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);

        // The hub happens to hold a collection of the same name it thinks a (disconnected) node owns
        // — exactly the confusion 31 D4 refuses: answering from the wrong data would be worse than
        // reporting the gap.
        hub.Ownership.Assign("node-that-is-away", ["shared-name"]);
        await hub.CreateCollectionAsync("shared-name", TestEmbeddings.Dimension);
        await hub.UpsertAsync("shared-name", new VectorUpsert("wrong", Vector: TestEmbeddings.Of("this must never come back")));

        var response = await hub.Client.PostAsync(
            "/api/retrieve/federated",
            JsonBody(new { collections = new[] { "shared-name" }, query = "anything" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var sources = body.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal("unavailable", sources[0].GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task MoreThanSixteenCollectionsIsA400()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);

        var names = Enumerable.Range(0, 17).Select(i => $"c{i}").ToArray();
        var response = await hub.Client.PostAsync(
            "/api/retrieve/federated",
            JsonBody(new { collections = names, query = "anything" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpContent JsonBody(object body) =>
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
