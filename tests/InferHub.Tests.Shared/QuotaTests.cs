using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Shared.ChatGpt;
using InferHub.Shared.Quota;

namespace InferHub.Tests;

/// <summary>
/// Phase 106: what a vendor says about its limits — on a response's headers and on the endpoints the
/// refresh asks — read into one shape, and never more than it said.
/// </summary>
public class QuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- headers --------------------------------------------------------------------------

    [Fact]
    public void CodexHeadersAreTheFiveHourAndTheWeeklyWindow()
    {
        var response = Response(HttpStatusCode.OK,
            ("x-codex-primary-used-percent", "37.5"),
            ("x-codex-primary-window-minutes", "300"),
            ("x-codex-primary-reset-at", Now.AddHours(2).ToUnixTimeSeconds().ToString()),
            ("x-codex-secondary-used-percent", "81"),
            ("x-codex-secondary-window-minutes", "10080"),
            ("x-codex-secondary-reset-after-seconds", "86400"));

        var windows = QuotaHeaders.Read(response.Headers, Now);

        var five = Assert.Single(windows, w => w.Name == "5h");
        Assert.Equal(QuotaWindow.Plan, five.Kind);
        Assert.Equal(37.5, five.UsedPercent);
        Assert.Equal(Now.AddHours(2), five.ResetsAtUtc);

        var weekly = Assert.Single(windows, w => w.Name == "weekly");
        Assert.Equal(81, weekly.UsedPercent);
        Assert.Equal(Now.AddDays(1), weekly.ResetsAtUtc);
        Assert.All(windows, w => Assert.Equal(QuotaWindow.FromHeaders, w.Source));
    }

    [Fact]
    public void OpenAiAndAnthropicRateHeadersAreUsedPercentOfTheLimit()
    {
        var openAi = QuotaHeaders.Read(Response(HttpStatusCode.OK,
            ("x-ratelimit-limit-requests", "500"),
            ("x-ratelimit-remaining-requests", "125"),
            ("x-ratelimit-reset-requests", "6m0s"),
            ("x-ratelimit-limit-tokens", "30000"),
            ("x-ratelimit-remaining-tokens", "29000")).Headers, Now);

        var requests = Assert.Single(openAi, w => w.Name == "requests");
        Assert.Equal(75, requests.UsedPercent);
        Assert.Equal(Now.AddMinutes(6), requests.ResetsAtUtc);
        Assert.Equal(125, requests.Remaining);
        Assert.Equal(3.3, Assert.Single(openAi, w => w.Name == "tokens").UsedPercent);

        var anthropic = QuotaHeaders.Read(Response(HttpStatusCode.OK,
            ("anthropic-ratelimit-input-tokens-limit", "100000"),
            ("anthropic-ratelimit-input-tokens-remaining", "10000"),
            ("anthropic-ratelimit-input-tokens-reset", "2026-10-15T12:01:00Z")).Headers, Now);

        var input = Assert.Single(anthropic);
        Assert.Equal("input-tokens", input.Name);
        Assert.Equal(90, input.UsedPercent);
        Assert.Equal(Now.AddMinutes(1), input.ResetsAtUtc);
    }

    [Fact]
    public void AResponseWithNoLimitHeadersSaysNothing()
        => Assert.Empty(QuotaHeaders.Read(Response(HttpStatusCode.OK, ("content-language", "en")).Headers, Now));

    [Theory]
    [InlineData("1s", 1)]
    [InlineData("6m0s", 360)]
    [InlineData("20ms", 0.02)]
    [InlineData("1h2m3.5s", 3723.5)]
    public void OpenAiResetDurationsParse(string text, double seconds)
        => Assert.Equal(seconds, QuotaHeaders.Duration(text)!.Value.TotalSeconds, 3);

    [Theory]
    [InlineData(300, "5h")]
    [InlineData(10080, "weekly")]
    [InlineData(1440, "daily")]
    [InlineData(2880, "2d")]
    [InlineData(45, "45m")]
    public void WindowsAreNamedTheWayTheVendorsPageNamesThem(int minutes, string name)
        => Assert.Equal(name, QuotaHeaders.WindowName(minutes));

    [Theory]
    [InlineData("""{"error":{"code":"subscription_sharing_usage_limit_exceeded","message":"limit"}}""", "subscription_sharing_usage_limit_exceeded")]
    [InlineData("""{"error":{"code":429,"message":"quota","status":"RESOURCE_EXHAUSTED"}}""", "429")]
    [InlineData("""{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", "rate_limit_error")]
    [InlineData("""{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""", "insufficient_quota")]
    public void TheVendorsOwnWordForARefusalIsKept(string body, string code)
        => Assert.Equal(code, QuotaHeaders.Error(body).Code);

    // ---- the tracker ----------------------------------------------------------------------

    [Fact]
    public void ADeclaredProviderIsListedBeforeItHasServedAnything()
    {
        var tracker = new QuotaTracker(new FixedTime(Now));
        tracker.Declare("openai", "openai-compatible");

        var quota = Assert.Single(tracker.Snapshot());
        Assert.Equal(QuotaLevel.Unknown, quota.Level);
        Assert.Empty(quota.Windows);
        Assert.Null(quota.Billing);
    }

    [Fact]
    public void LevelsFollowTheFullestWindowAndARefusalClearsOnTheNextSuccess()
    {
        var tracker = new QuotaTracker(new FixedTime(Now));
        tracker.Declare("cg", "chatgpt");

        tracker.Observe("cg", Response(HttpStatusCode.OK, ("x-codex-primary-used-percent", "40"), ("x-codex-primary-window-minutes", "300")));
        Assert.Equal(QuotaLevel.Ok, tracker.Of("cg")!.Level);

        tracker.Observe("cg", Response(HttpStatusCode.OK, ("x-codex-secondary-used-percent", "85"), ("x-codex-secondary-window-minutes", "10080")));
        Assert.Equal(QuotaLevel.Warn, tracker.Of("cg")!.Level);

        var refused = Response(HttpStatusCode.TooManyRequests, ("retry-after", "600"));
        tracker.Observe("cg", refused, "subscription_sharing_usage_limit_exceeded", "the plan's limit");
        var limited = tracker.Of("cg")!;
        Assert.Equal(QuotaLevel.Limit, limited.Level);
        Assert.Contains("subscription_sharing_usage_limit_exceeded", limited.LimitReached);
        Assert.Equal(Now.AddMinutes(10), limited.RetryAtUtc);

        tracker.Observe("cg", Response(HttpStatusCode.OK));
        Assert.Null(tracker.Of("cg")!.LimitReached);
        Assert.Equal(QuotaLevel.Warn, tracker.Of("cg")!.Level);
    }

    [Fact]
    public void AWindowWhoseResetHasPassedReadsAsEmptyRatherThanFull()
    {
        var time = new FixedTime(Now);
        var tracker = new QuotaTracker(time);
        tracker.SetWindows("cg", [new QuotaWindow("5h", QuotaWindow.Plan, 100, 300, Now.AddMinutes(5), QuotaWindow.FromUsageEndpoint, Now)], "plus");
        Assert.Equal(QuotaLevel.Limit, tracker.Of("cg")!.Level);

        time.Now = Now.AddMinutes(6);
        var window = Assert.Single(tracker.Of("cg")!.Windows);
        Assert.Equal(0, window.UsedPercent);
        Assert.Equal("plus", tracker.Of("cg")!.Plan);
    }

    [Fact]
    public void BillingIsSpendOverBudgetAndABudgetWithNoSpendSaysWhy()
    {
        var tracker = new QuotaTracker(new FixedTime(Now));
        tracker.Declare("openai", "openai-compatible", new QuotaBillingOptions { MonthlyBudgetUsd = 50, WarnPercent = 75 });

        var before = tracker.Of("openai")!;
        Assert.Equal(50m, before.Billing!.BudgetUsd);
        Assert.Null(before.Billing.SpentUsd);
        Assert.Contains("Billing:AdminApiKey", before.Note);

        tracker.SetBilling("openai", 40m, QuotaProbes.OpenAiCosts, "2026-10");
        var after = tracker.Of("openai")!;
        Assert.Equal(80, after.Billing!.UsedPercent);
        Assert.Equal(QuotaLevel.Warn, after.Level);
        Assert.Null(after.Note);

        tracker.SetBilling("openai", 51m, QuotaProbes.OpenAiCosts, "2026-10");
        Assert.Equal(QuotaLevel.Limit, tracker.Of("openai")!.Level);
    }

    [Fact]
    public void AnUndeclaredSubjectThatSaysNothingIsNotListed()
    {
        // A vLLM behind an openai engine: no headers, no refusal, nothing to show.
        var tracker = new QuotaTracker(new FixedTime(Now));
        tracker.Observe("vllm", Response(HttpStatusCode.OK));
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void RetainForgetsProvidersNoLongerConfigured()
    {
        var tracker = new QuotaTracker(new FixedTime(Now));
        tracker.Declare("a", "gemini");
        tracker.Declare("b", "gemini");
        tracker.Retain(["b"]);
        Assert.Equal("b", Assert.Single(tracker.Snapshot()).Subject);
    }

    // ---- the observing handler ------------------------------------------------------------

    [Fact]
    public async Task TheHandlerReadsARefusalAndTheCallerStillReadsTheSameBody()
    {
        const string body = """{"error":{"code":"insufficient_quota","message":"You exceeded your current quota"}}""";
        var tracker = new QuotaTracker(new FixedTime(Now));
        using var http = new HttpClient(new QuotaObservingHandler(tracker)
        {
            InnerHandler = new Scripted(_ => Response(HttpStatusCode.TooManyRequests, body))
        });

        string read;

        using (QuotaScope.Enter("openai"))
        {
            using var response = await http.GetAsync("https://api.openai.com/v1/chat/completions");
            read = await response.Content.ReadAsStringAsync();
        }

        Assert.Equal(body, read);
        var quota = Assert.Single(tracker.Snapshot());
        Assert.Equal(QuotaLevel.Limit, quota.Level);
        Assert.Equal("429 insufficient_quota: You exceeded your current quota", quota.LimitReached);
    }

    [Fact]
    public async Task WithoutAScopeTheHandlerRecordsNothing()
    {
        var tracker = new QuotaTracker(new FixedTime(Now));
        using var http = new HttpClient(new QuotaObservingHandler(tracker)
        {
            InnerHandler = new Scripted(_ => Response(HttpStatusCode.OK, ("x-codex-primary-used-percent", "50")))
        });

        using var response = await http.GetAsync("https://example.test/");
        Assert.Empty(tracker.Snapshot());
    }

    // ---- the probes -----------------------------------------------------------------------

    [Fact]
    public async Task OpenAiCostsAreSummedAcrossPagesWithTheAdminKey()
    {
        var seen = new List<HttpRequestMessage>();
        using var http = new HttpClient(new Scripted(request =>
        {
            seen.Add(request);
            return request.RequestUri!.Query.Contains("page=p2")
                ? Response(HttpStatusCode.OK, """{"object":"page","data":[{"results":[{"amount":{"value":1.25,"currency":"usd"}}]}],"has_more":false,"next_page":null}""")
                : Response(HttpStatusCode.OK, """{"object":"page","data":[{"results":[{"amount":{"value":2.5,"currency":"usd"}},{"amount":{"value":0.25,"currency":"usd"}}]},{"results":[]}],"has_more":true,"next_page":"p2"}""");
        }));

        var spent = await QuotaProbes.OpenAiCostsAsync(http, "https://api.openai.com/v1", "sk-admin-x", QuotaProbes.MonthStart(Now), CancellationToken.None);

        Assert.Equal(4.0m, spent);
        Assert.Equal(2, seen.Count);
        Assert.Equal("Bearer sk-admin-x", seen[0].Headers.Authorization!.ToString());
        Assert.StartsWith("https://api.openai.com/v1/organization/costs?start_time=" + new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), seen[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task AnthropicCostsAreCentsAndComeBackAsDollars()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Scripted(request =>
        {
            seen = request;
            return Response(HttpStatusCode.OK, """{"data":[{"starting_at":"2026-10-01T00:00:00Z","ending_at":"2026-10-02T00:00:00Z","results":[{"amount":"12345.6","currency":"USD"}]}],"has_more":false,"next_page":null}""");
        }));

        var spent = await QuotaProbes.AnthropicCostAsync(http, "https://api.anthropic.com/v1", "sk-ant-admin", "2023-06-01", QuotaProbes.MonthStart(Now), CancellationToken.None);

        Assert.Equal(123.456m, spent);
        Assert.Equal("https://api.anthropic.com/v1/organizations/cost_report?starting_at=2026-10-01T00%3A00%3A00Z&bucket_width=1d&limit=31", seen!.RequestUri!.ToString());
        Assert.Equal("sk-ant-admin", seen.Headers.GetValues("x-api-key").Single());
        Assert.Null(seen.Headers.Authorization);
    }

    [Fact]
    public async Task ARefusedProbeIsASentenceNamingTheStatus()
    {
        using var http = new HttpClient(new Scripted(_ => Response(HttpStatusCode.Unauthorized, """{"error":{"message":"Missing scopes: api.usage.read","type":"invalid_request_error"}}""")));

        var ex = await Assert.ThrowsAsync<QuotaProbeException>(() =>
            QuotaProbes.OpenAiCostsAsync(http, "https://api.openai.com/v1", "sk-proj-not-admin", Now, CancellationToken.None));

        Assert.Equal("OpenAI's costs API answered 401 invalid_request_error: Missing scopes: api.usage.read", ex.Message);
    }

    [Fact]
    public void TheChatGptUsageEndpointIsAPlanAndTwoWindows()
    {
        using var document = JsonDocument.Parse("""
        {"plan_type":"plus","rate_limit":{"allowed":true,"limit_reached":false,
          "primary_window":{"used_percent":12,"limit_window_seconds":18000,"reset_after_seconds":3600,"reset_at":RESET},
          "secondary_window":{"used_percent":64,"limit_window_seconds":604800,"reset_after_seconds":200000}}}
        """.Replace("RESET", Now.AddHours(1).ToUnixTimeSeconds().ToString()));

        var usage = QuotaProbes.ParseChatGptUsage(document.RootElement, Now);

        Assert.Equal("plus", usage.Plan);
        Assert.Equal(12, Assert.Single(usage.Windows, w => w.Name == "5h").UsedPercent);
        var weekly = Assert.Single(usage.Windows, w => w.Name == "weekly");
        Assert.Equal(64, weekly.UsedPercent);
        Assert.Equal(Now.AddSeconds(200000), weekly.ResetsAtUtc);
    }

    [Fact]
    public async Task OpenRouterReportsTheKeysOwnLimitWhenNoBudgetIsConfigured()
    {
        using var http = new HttpClient(new Scripted(_ => Response(HttpStatusCode.OK,
            """{"data":{"label":"k","usage":30,"usage_monthly":12.5,"limit":40,"limit_remaining":10,"limit_reset":"monthly"}}""")));
        var tracker = new QuotaTracker(new FixedTime(Now));
        var refresher = new QuotaRefresher(tracker, () => http, new QuotaOptions(), new FixedTime(Now));

        await refresher.RefreshAsync([new QuotaSubject("or", "openrouter", "https://openrouter.ai/api/v1", "sk-or", new QuotaBillingOptions())], CancellationToken.None);

        var billing = tracker.Of("or")!.Billing!;
        Assert.Equal(30m, billing.SpentUsd);
        Assert.Equal(40m, billing.BudgetUsd);
        Assert.Equal("vendor", billing.BudgetSource);
        Assert.Equal("monthly", billing.Period);
        Assert.Equal(75, billing.UsedPercent);
    }

    [Fact]
    public async Task ARefusingChatGptUsageEndpointLeavesANoteAndIsNotAskedAgainForAnHour()
    {
        var calls = 0;
        using var http = new HttpClient(new Scripted(_ =>
        {
            calls++;
            return Response(HttpStatusCode.Forbidden, "<html>challenge</html>");
        }));
        var tracker = new QuotaTracker(new FixedTime(Now));
        var refresher = new QuotaRefresher(tracker, () => http, new QuotaOptions(), new FixedTime(Now));
        var session = SignedInSession(out var directory);

        try
        {
            var subject = new QuotaSubject("cg", "chatgpt", null, null, null, ChatGpt: session);
            await refresher.RefreshAsync([subject], CancellationToken.None);
            await refresher.RefreshAsync([subject], CancellationToken.None);

            Assert.Equal(1, calls);
            var quota = tracker.Of("cg")!;
            Assert.Contains("answered 403", quota.Note);
            Assert.Contains("headers of the next real request", quota.Note);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheChatGptProbeSendsTheAccountIdFromTheStoredIdToken()
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Scripted(request =>
        {
            seen = request;
            return Response(HttpStatusCode.OK, """{"plan_type":"pro","rate_limit":{"primary_window":{"used_percent":5,"limit_window_seconds":18000}}}""");
        }));
        var tracker = new QuotaTracker(new FixedTime(Now));
        var refresher = new QuotaRefresher(tracker, () => http, new QuotaOptions(), new FixedTime(Now));
        var session = SignedInSession(out var directory);

        try
        {
            await refresher.RefreshAsync([new QuotaSubject("cg", "chatgpt", null, null, null, ChatGpt: session)], CancellationToken.None);

            Assert.Equal(QuotaOptions.DefaultChatGptUsageUrl, seen!.RequestUri!.ToString());
            Assert.Equal("Bearer access-1", seen.Headers.Authorization!.ToString());
            Assert.Equal("acct-123", seen.Headers.GetValues("ChatGPT-Account-Id").Single());
            Assert.Equal("pro", tracker.Of("cg")!.Plan);
            Assert.Equal(5, Assert.Single(tracker.Of("cg")!.Windows).UsedPercent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AnEmptyUsageUrlSendsNothing()
    {
        var calls = 0;
        using var http = new HttpClient(new Scripted(_ => { calls++; return Response(HttpStatusCode.OK, "{}"); }));
        var tracker = new QuotaTracker(new FixedTime(Now));
        var refresher = new QuotaRefresher(tracker, () => http, new QuotaOptions { ChatGptUsageUrl = "" }, new FixedTime(Now));
        var session = SignedInSession(out var directory);

        try
        {
            await refresher.RefreshAsync([new QuotaSubject("cg", "chatgpt", null, null, null, ChatGpt: session)], CancellationToken.None);
            Assert.Equal(0, calls);
            Assert.Equal(QuotaLevel.Unknown, tracker.Of("cg")!.Level);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    private static ChatGptSession SignedInSession(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "inferhub-quota-" + Guid.NewGuid().ToString("N"));
        var store = new ChatGptCredentialStore(directory);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                """{"sub":"user-1","https://api.openai.com/auth":{"chatgpt_account_id":"acct-123","chatgpt_plan_type":"pro"}}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        store.Save(new ChatGptAccountsFile
        {
            Active = "r1",
            Registrations =
            [
                new ChatGptRegistration
                {
                    Id = "r1",
                    ClientId = "oaiapp_1",
                    Subject = "user-1",
                    IdToken = $"e30.{payload}.sig",
                    AccessToken = "access-1",
                    RefreshToken = "refresh-1",
                    AccessExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                    Scopes = [ChatGptOAuth.PlanUsageScope]
                }
            ]
        });

        return new ChatGptSession(store, () => new HttpClient(), IdTokenValidator.Over(new HttpClient()));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, params (string Name, string Value)[] headers)
        => Response(status, "{}", headers);

    private static HttpResponseMessage Response(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    private sealed class Scripted(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
