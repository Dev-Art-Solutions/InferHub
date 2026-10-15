using InferHub.Node.Backends;
using InferHub.Node.Quota;
using InferHub.Shared.Quota;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 106 on the node: which of its backends are a vendor whose limits are worth showing. A vLLM
/// behind an <c>openai</c> type is not; OpenAI itself is, and so is anything its operator gave a budget.
/// </summary>
public class QuotaNodeTests
{
    [Theory]
    [InlineData(BackendOptions.Gemini, null, true)]
    [InlineData(BackendOptions.Anthropic, null, true)]
    [InlineData(BackendOptions.OpenRouter, null, true)]
    [InlineData(BackendOptions.OpenAi, "https://api.openai.com/v1", true)]
    [InlineData(BackendOptions.OpenAi, "http://localhost:8000/v1", false)]
    [InlineData(BackendOptions.Ollama, null, false)]
    [InlineData(BackendOptions.LlamaCpp, "http://127.0.0.1:8080/v1", false)]
    public void ASingleBackendIsListedOnlyWhenItIsAVendor(string type, string? baseUrl, bool listed)
    {
        var subjects = Service(new BackendOptions { Type = type }, new UpstreamBackendOptions { BaseUrl = baseUrl, ApiKey = "k" }).Subjects();

        if (listed)
        {
            var subject = Assert.Single(subjects);
            Assert.Equal(type, subject.Subject);
            Assert.Equal("k", subject.ApiKey);
        }
        else
        {
            Assert.Empty(subjects);
        }
    }

    [Fact]
    public void ALocalOpenAiServerWithABudgetIsListedBecauseItsOperatorAskedForIt()
    {
        var subjects = Service(
            new BackendOptions { Type = BackendOptions.OpenAi },
            new UpstreamBackendOptions { BaseUrl = "https://proxy.example/v1", Billing = new QuotaBillingOptions { MonthlyBudgetUsd = 10 } }).Subjects();

        Assert.Equal(10m, Assert.Single(subjects).Billing!.MonthlyBudgetUsd);
    }

    [Fact]
    public void EnginesAreListedByTheirOwnNamesAndChatGptOnce()
    {
        var backend = new BackendOptions();
        backend.Engines["cloud"] = new EngineOptions { Type = BackendOptions.OpenAi, BaseUrl = "https://api.openai.com/v1", ApiKey = "sk-1" };
        backend.Engines["vllm"] = new EngineOptions { Type = BackendOptions.OpenAi, BaseUrl = "http://localhost:8000/v1" };
        backend.Engines["local"] = new EngineOptions { Type = BackendOptions.Ollama };
        backend.Engines["plan"] = new EngineOptions { Type = BackendOptions.ChatGpt };

        var subjects = Service(backend, new UpstreamBackendOptions()).Subjects();

        Assert.Equal(["chatgpt", "cloud"], subjects.Select(s => s.Subject).Order().ToArray());
        Assert.Equal("sk-1", subjects.Single(s => s.Subject == "cloud").ApiKey);
    }

    [Fact]
    public void AnUpstreamBackendNamesItsOwnSubjectOnlyForAVendor()
    {
        static UpstreamBackend Backend(string type, string? subject = null) => new(
            new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            Options.Create(new BackendOptions { Type = type }),
            Options.Create(new UpstreamBackendOptions()),
            NullLogger<UpstreamBackend>.Instance,
            quotaSubject: subject);

        Assert.Equal("gemini", Backend(BackendOptions.Gemini).QuotaSubject);
        Assert.Null(Backend(BackendOptions.LlamaCpp).QuotaSubject);
        Assert.Equal("cloud", Backend(BackendOptions.OpenAi, "cloud").QuotaSubject);
    }

    private static NodeQuotaService Service(BackendOptions backend, UpstreamBackendOptions upstream)
        => new(
            new QuotaTracker(),
            Options.Create(backend),
            Options.Create(upstream),
            Options.Create(new QuotaOptions()),
            new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<NodeQuotaService>.Instance);
}
