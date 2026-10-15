using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using InferHub.Shared.Quota;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InferHub.Tests;

/// <summary>
/// Phase 106 on a real hub: a chat request through the real <see cref="ProviderDispatcher"/> and the real
/// pooled client, a vendor answering with limit headers, and <c>/api/admin/quota</c> reading back what
/// the vendor said — the coordinator's own beside each node's own.
/// </summary>
public class QuotaProviderTests
{
    private const string ChatJob = """
    {"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}
    """;

    private const string Answer = """
    {"id":"c","created":0,"model":"gpt","choices":[{"index":0,"message":{"role":"assistant","content":"Hello."},"finish_reason":"stop"}],
     "usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}
    """;

    [Fact]
    public async Task TheVendorsHeadersOnARealDispatchReachTheAdminView()
    {
        var vendor = new Vendor(_ => Ok(Answer,
            ("x-ratelimit-limit-requests", "100"),
            ("x-ratelimit-remaining-requests", "10")));

        await using var hub = await Start(vendor, budget: 20m);

        using var chat = await hub.Client.PostAsync("/api/chat", new StringContent(ChatJob, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Equal("provider:openai", chat.Headers.GetValues("X-InferHub-Served-By").Single());

        using var document = JsonDocument.Parse(await hub.Client.GetStringAsync("/api/admin/quota"));
        var openai = document.RootElement.GetProperty("coordinator").EnumerateArray().Single();

        Assert.Equal("openai", openai.GetProperty("subject").GetString());
        Assert.Equal("warn", openai.GetProperty("level").GetString());
        var requests = openai.GetProperty("windows").EnumerateArray().Single();
        Assert.Equal("requests", requests.GetProperty("name").GetString());
        Assert.Equal(90, requests.GetProperty("usedPercent").GetDouble());
        Assert.Equal(20m, openai.GetProperty("billing").GetProperty("budgetUsd").GetDecimal());
    }

    [Fact]
    public async Task AVendorRefusalForQuotaIsShownAndTheCallerStillGetsTheVendorsSentence()
    {
        var vendor = new Vendor(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""",
                Encoding.UTF8, "application/json")
        });

        await using var hub = await Start(vendor);

        using var chat = await hub.Client.PostAsync("/api/chat", new StringContent(ChatJob, Encoding.UTF8, "application/json"));
        Assert.Contains("exceeded your current quota", await chat.Content.ReadAsStringAsync());

        var quota = await hub.Client.GetFromJsonAsync<JsonElement>("/api/admin/quota");
        var openai = quota.GetProperty("coordinator").EnumerateArray().Single();
        Assert.Equal("limit", openai.GetProperty("level").GetString());
        Assert.StartsWith("429 insufficient_quota", openai.GetProperty("limitReached").GetString());
    }

    [Fact]
    public async Task ARefreshAsksTheCostsApiWithTheAdminKeyAndNeverTheInferenceKey()
    {
        var seen = new List<string>();
        var vendor = new Vendor(request =>
        {
            seen.Add($"{request.RequestUri!.AbsolutePath} {request.Headers.Authorization}");
            return Ok("""{"object":"page","data":[{"results":[{"amount":{"value":15,"currency":"usd"}}]}],"has_more":false,"next_page":null}""");
        });

        await using var hub = await Start(vendor, budget: 20m, adminKey: "sk-admin-1");

        var quota = await (await hub.Client.PostAsync("/api/admin/quota/refresh", null)).Content.ReadFromJsonAsync<JsonElement>();
        var billing = quota.GetProperty("coordinator").EnumerateArray().Single().GetProperty("billing");

        Assert.Equal(15m, billing.GetProperty("spentUsd").GetDecimal());
        Assert.Equal(75, billing.GetProperty("usedPercent").GetDouble());
        Assert.Equal(QuotaProbes.OpenAiCosts, billing.GetProperty("spendSource").GetString());
        Assert.Contains("/v1/organization/costs Bearer sk-admin-1", seen);
        Assert.DoesNotContain(seen, line => line.Contains("sk-inference"));
    }

    [Fact]
    public async Task ANodesOwnReportIsListedUnderThatNodeAndNotAddedToTheHubs()
    {
        var vendor = new Vendor(_ => Ok(Answer));
        await using var hub = await Start(vendor);

        hub.Services.GetRequiredService<NodeQuotaRegistry>().Report(new NodeQuotaState("node-1",
        [
            new ProviderQuota("chatgpt", "chatgpt", QuotaLevel.Warn, "plus",
                [new QuotaWindow("5h", QuotaWindow.Plan, 85, 300, null, QuotaWindow.FromHeaders, DateTimeOffset.UtcNow)],
                null, null, null, null, null, DateTimeOffset.UtcNow)
        ], DateTimeOffset.UtcNow));

        var quota = await hub.Client.GetFromJsonAsync<JsonElement>("/api/admin/quota");
        var node = quota.GetProperty("nodes").EnumerateArray().Single();

        Assert.Equal("parity-node", node.GetProperty("name").GetString());
        var chatgpt = node.GetProperty("providers").EnumerateArray().Single();
        Assert.Equal("plus", chatgpt.GetProperty("plan").GetString());
        Assert.Equal(85, chatgpt.GetProperty("windows")[0].GetProperty("usedPercent").GetDouble());
        Assert.DoesNotContain(quota.GetProperty("coordinator").EnumerateArray(), q => q.GetProperty("subject").GetString() == "chatgpt");
    }

    // ---- fixture ---------------------------------------------------------------------------

    private static async Task<QuotaHub> Start(Vendor vendor, decimal? budget = null, string? adminKey = null)
    {
        var providers = new ProviderOptions();
        var openai = new ProviderDefinition
        {
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-inference",
            Policy = "only",
            Billing = new QuotaBillingOptions { MonthlyBudgetUsd = budget, AdminApiKey = adminKey }
        };
        openai.ModelMap["llama3"] = "gpt-5-mini";
        providers.Entries["openai"] = openai;

        WebApplication? app = null;

        var hub = await HubHost.StartAsync(
            blockingResponse: "{}",
            providers: providers,
            configure: builder =>
            {
                builder.Services.Configure<QuotaOptions>(options => options.RefreshSeconds = 3600);
                builder.Services.AddSingleton(_ => new QuotaTracker());
                builder.Services.AddHttpClient(ProviderDispatcher.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => vendor)
                    .AddHttpMessageHandler(services => new QuotaObservingHandler(services.GetRequiredService<QuotaTracker>()));
                builder.Services.AddHttpClient(CoordinatorQuotaService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => vendor);
                builder.Services.AddSingleton<NodeQuotaRegistry>();
                builder.Services.AddSingleton<CoordinatorQuotaService>();
            },
            map: built =>
            {
                app = built;
                built.MapQuotaEndpoints();
            });

        return new QuotaHub(hub, app!.Services);
    }

    private static HttpResponseMessage Ok(string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private sealed record QuotaHub(HubHost Host, IServiceProvider Services) : IAsyncDisposable
    {
        public HttpClient Client => Host.Client;

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class Vendor(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));

        // Pooled handlers are disposed by the factory; this one is the test's.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
