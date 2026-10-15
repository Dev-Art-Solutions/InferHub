using InferHub.Node.Backends;
using InferHub.Node.Backends.ChatGpt;
using InferHub.Shared.Quota;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Quota;

/// <summary>
/// This node's own cloud providers, asked every <c>Quota:RefreshSeconds</c> (phase 106): its backend when
/// that is a vendor, its <c>openai</c> engines that are OpenAI itself, and its own ChatGPT sign-in. The
/// coordinator hears the result on <c>ReportQuotaState</c> and never asks (44 D6).
/// </summary>
public sealed class NodeQuotaService(
    QuotaTracker tracker,
    IOptions<BackendOptions> backend,
    IOptions<UpstreamBackendOptions> upstream,
    IOptions<QuotaOptions> options,
    IHttpClientFactory httpClients,
    IServiceProvider services,
    ILogger<NodeQuotaService> logger) : BackgroundService
{
    public const string HttpClientName = "inferhub-quota";

    private QuotaRefresher? refresher;

    public IReadOnlyList<QuotaSubject> Subjects()
    {
        var subjects = new List<QuotaSubject>();
        var type = backend.Value.Normalized();

        if (backend.Value.IsMulti)
        {
            foreach (var (name, engine) in backend.Value.Engines)
            {
                var engineType = engine.NormalizedType();

                if (engineType == BackendOptions.ChatGpt)
                {
                    AddChatGpt(subjects);
                }
                else if (engineType == BackendOptions.OpenAi && IsOpenAi(engine.BaseUrl, engine.Billing))
                {
                    subjects.Add(new QuotaSubject(name, BackendOptions.OpenAi, engine.BaseUrl, engine.ApiKey, engine.Billing));
                }
            }

            return subjects;
        }

        if (type == BackendOptions.ChatGpt)
        {
            AddChatGpt(subjects);
        }
        else if (type is BackendOptions.OpenRouter or BackendOptions.Anthropic or BackendOptions.Gemini
                 || (type == BackendOptions.OpenAi && IsOpenAi(upstream.Value.BaseUrl, upstream.Value.Billing)))
        {
            var options = upstream.Value;
            subjects.Add(new QuotaSubject(type, type, options.ResolvedBaseUrl(type), options.ApiKey, options.Billing, options.ResolvedAnthropicVersion()));
        }

        return subjects;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        refresher ??= new QuotaRefresher(tracker, () => httpClients.CreateClient(HttpClientName), options.Value);
        await refresher.RefreshAsync(Subjects(), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not refresh this node's provider limits");
            }

            try
            {
                await Task.Delay(options.Value.RefreshInterval(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void AddChatGpt(List<QuotaSubject> subjects)
    {
        if (subjects.Any(s => s.Subject == ChatGptBackend.QuotaSubject))
        {
            return;
        }

        subjects.Add(new QuotaSubject(ChatGptBackend.QuotaSubject, BackendOptions.ChatGpt, null, null, null,
            ChatGpt: services.GetService<NodeChatGpt>()?.Session));
    }

    /// <summary>
    /// An <c>openai</c> backend is usually a vLLM on this box, which has no limit to show. It is listed
    /// when it is OpenAI itself, or when its operator gave it a budget.
    /// </summary>
    private static bool IsOpenAi(string? baseUrl, QuotaBillingOptions billing)
        => billing.HasBudget
           || !string.IsNullOrWhiteSpace(billing.AdminApiKey)
           || (baseUrl?.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ?? false);
}
