using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Phase 65, across two real sockets. The hub is the shipped one on real Kestrel with a real
/// <see cref="ProviderDispatcher"/> and a real <c>HttpClient</c>; the vendor is a second Kestrel
/// that records what arrived. Nothing between them is stubbed, which is the only way to answer the
/// question this phase asks — <em>where did the prompt actually go</em> — rather than the question a
/// fake dispatcher can answer, which is what we already believed.
/// </summary>
public class ProviderRoutingTests
{
    private const string NodeAnswer = """
    {"model":"smart","created_at":"2026-08-25T00:00:00Z","message":{"role":"assistant","content":"From the fleet."},"done":true}
    """;

    private const string VendorAnswer = """
    {
      "id": "chatcmpl-1", "created": 0, "model": "remote-smart",
      "choices": [{"index":0,"message":{"role":"assistant","content":"From the vendor."},"finish_reason":"stop"}],
      "usage": {"prompt_tokens": 3, "completion_tokens": 4, "total_tokens": 7}
    }
    """;

    private const string ChatBody = """
    {"model":"smart","messages":[{"role":"user","content":"hello"}],"stream":false}
    """;

    [Fact]
    public async Task APreferredProviderAnswersWhileAnIdleNodeHoldsTheSameModel()
    {
        // The sentence the whole track exists for. Every release up to v3.32 would have served this
        // from the node, because an upstream was only ever consulted after routing had failed.
        await using var vendor = await Vendor.StartAsync();
        await using var hub = await StartHubAsync(vendor, ProviderPolicy.Prefer);

        var response = await hub.Client.PostAsync("/api/chat", Json(ChatBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("provider:vendor", response.Headers.GetValues(InferenceCore.ServedByHeader).Single());

        var sent = Assert.Single(vendor.Requests);

        // The vendor is asked by *its* name for the model, and the caller is answered in theirs.
        Assert.Equal("remote-smart", sent.Model);
        Assert.Equal("smart", (await Body(response)).GetProperty("model").GetString());
        Assert.Contains("From the vendor.", (await Body(response)).GetProperty("message").GetProperty("content").GetString());

        // …and the fleet never saw it.
        Assert.Null(hub.LastJobJson);
    }

    [Fact]
    public async Task TheNodeSteerKeepsOnePromptOnTheFleetWithoutTouchingConfig()
    {
        // 65 D4's direction that matters: the same hub, the same preferred provider, one header.
        await using var vendor = await Vendor.StartAsync();
        await using var hub = await StartHubAsync(vendor, ProviderPolicy.Prefer);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Json(ChatBody) };
        request.Headers.Add(ProviderSteer.HeaderName, ProviderSteer.NodeValue);

        var response = await hub.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(InferenceCore.ServedByNode, response.Headers.GetValues(InferenceCore.ServedByHeader).Single());
        Assert.Empty(vendor.Requests);
        Assert.NotNull(hub.LastJobJson);
    }

    [Fact]
    public async Task ASteerNobodyCanHonourIsRefusedAndNothingLeavesTheHub()
    {
        await using var vendor = await Vendor.StartAsync();
        await using var hub = await StartHubAsync(vendor, ProviderPolicy.Prefer);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Json(ChatBody) };
        request.Headers.Add(ProviderSteer.HeaderName, "not-configured");

        var response = await hub.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Refused before anything moved: not the vendor, and not the fleet either.
        Assert.Empty(vendor.Requests);
        Assert.Null(hub.LastJobJson);

        var error = (await Body(response)).GetProperty("error").GetString()!;
        Assert.Contains("not-configured", error);
        Assert.DoesNotContain("vendor", error);
    }

    [Fact]
    public async Task TheSteerIsHonouredOnTheOpenAiSurfaceToo()
    {
        // Two client dialects, one parser and one decision — or the answer to "whose servers saw my
        // prompt" would depend on which endpoint the client happened to use.
        await using var vendor = await Vendor.StartAsync();
        await using var hub = await StartHubAsync(vendor, ProviderPolicy.NoNode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = Json("""{"model":"smart","messages":[{"role":"user","content":"hello"}],"stream":false}""")
        };
        request.Headers.Add(ProviderSteer.HeaderName, "vendor");

        var response = await hub.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("provider:vendor", response.Headers.GetValues(InferenceCore.ServedByHeader).Single());
        Assert.Single(vendor.Requests);
        Assert.Null(hub.LastJobJson);
    }

    [Fact]
    public async Task AnOverflowProviderIsStillTheSecondChoice()
    {
        // Guard the guard: with the default policy the same fleet, the same vendor and the same
        // request go to the node — so the preferred case above is measuring the policy rather than
        // the fixture.
        await using var vendor = await Vendor.StartAsync();
        await using var hub = await StartHubAsync(vendor, ProviderPolicy.NoNode);

        var response = await hub.Client.PostAsync("/api/chat", Json(ChatBody));

        Assert.Equal(InferenceCore.ServedByNode, response.Headers.GetValues(InferenceCore.ServedByHeader).Single());
        Assert.Empty(vendor.Requests);
    }

    [Fact]
    public async Task AModelOnlyAProviderHoldsIsDiscoverableOnBothSurfaces()
    {
        // Until v3.33 `cloud-only` was a model a client could call and could not see.
        await using var vendor = await Vendor.StartAsync();
        await using var hub = await StartHubAsync(vendor, ProviderPolicy.Prefer);

        var tags = await hub.Client.GetFromJsonAsync<JsonElement>("/api/tags");
        var names = tags.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("name").GetString());

        Assert.Equal(["cloud-only", "smart"], names);

        var claimed = tags.GetProperty("models").EnumerateArray()
            .Single(model => model.GetProperty("name").GetString() == "cloud-only");

        // A zero you constructed to fill a field is not a measurement.
        Assert.Equal(JsonValueKind.Null, claimed.GetProperty("digest").ValueKind);
        Assert.Equal(JsonValueKind.Null, claimed.GetProperty("size").ValueKind);

        var models = await hub.Client.GetFromJsonAsync<JsonElement>("/v1/models");
        var cloud = models.GetProperty("data").EnumerateArray()
            .Single(model => model.GetProperty("id").GetString() == "cloud-only");

        // Chat and nothing else: the hub has no provider arm for embeddings, and listing one would
        // be a promise answered with a 404.
        Assert.Equal(["chat"], cloud.GetProperty("capabilities").EnumerateArray().Select(kind => kind.GetString()));

        var one = await hub.Client.GetFromJsonAsync<JsonElement>("/v1/models/cloud-only");
        Assert.Equal("cloud-only", one.GetProperty("id").GetString());
    }

    // ---- harness -----------------------------------------------------------------------

    private static async Task<HubHost> StartHubAsync(Vendor vendor, string policy)
    {
        var definition = new ProviderDefinition
        {
            Type = ProviderDefinition.TypeOpenAiCompatible,
            BaseUrl = vendor.BaseUrl,
            ApiKey = "vendor-key",
            Policy = policy,
            ModelMap =
            {
                ["smart"] = "remote-smart",
                ["cloud-only"] = "remote-cloud-only"
            }
        };

        var providers = new ProviderOptions();
        providers.Entries["vendor"] = definition;

        return await HubHost.StartAsync(
            NodeAnswer,
            models: [new ModelInfo("smart", "sha256:local", 4242)],
            providers: providers);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>
    /// A vendor on a real port. It answers the OpenAI shape and records what it was asked, which is
    /// the assertion that matters: a test that only reads the hub's response header cannot tell a
    /// route that was taken from a route that was reported.
    /// </summary>
    private sealed class Vendor : IAsyncDisposable
    {
        private WebApplication app = null!;

        public List<Sent> Requests { get; } = [];

        public string BaseUrl => app.Urls.First().TrimEnd('/');

        public static async Task<Vendor> StartAsync()
        {
            var vendor = new Vendor();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            vendor.app = builder.Build();

            vendor.app.MapPost("/chat/completions", async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                lock (vendor.Requests)
                {
                    vendor.Requests.Add(new Sent(
                        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString(),
                        context.Request.Headers.Authorization.ToString()));
                }

                return Results.Text(VendorAnswer, "application/json");
            });

            await vendor.app.StartAsync();
            return vendor;
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();

        internal sealed record Sent(string? Model, string Authorization);
    }
}
