using System.Collections.Concurrent;
using InferHub.Coordinator.ChatGpt;
using InferHub.Shared.Quota;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// What each node last said about its own providers' limits (phase 106) — phase 45's registry once more.
/// In memory (rule 4): a coordinator restart forgets it and every node re-reports at registration.
/// </summary>
public sealed class NodeQuotaRegistry
{
    private readonly ConcurrentDictionary<string, NodeQuotaState> states = new(StringComparer.OrdinalIgnoreCase);

    public void Report(NodeQuotaState state)
    {
        if (!string.IsNullOrWhiteSpace(state.NodeId))
        {
            states[state.NodeId.Trim()] = state;
        }
    }

    public NodeQuotaState? Of(string nodeId) =>
        string.IsNullOrWhiteSpace(nodeId) ? null : states.GetValueOrDefault(nodeId.Trim());

    public void Forget(string nodeId)
    {
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            states.TryRemove(nodeId.Trim(), out _);
        }
    }
}

/// <summary>
/// The coordinator's own providers, asked every <c>Quota:RefreshSeconds</c> (phase 106) and on demand
/// from the console. The headers half needs no loop: <see cref="QuotaObservingHandler"/> reads them off
/// every dispatch.
/// </summary>
public sealed class CoordinatorQuotaService(
    QuotaTracker tracker,
    IProviderRegistry providers,
    IOptions<QuotaOptions> options,
    IHttpClientFactory httpClients,
    ILogger<CoordinatorQuotaService> logger,
    ChatGptAccounts? chatGpt = null) : BackgroundService
{
    public const string HttpClientName = "inferhub-quota";

    private readonly SemaphoreSlim gate = new(1, 1);
    private QuotaRefresher? refresher;

    public QuotaOptions Options => options.Value;

    public DateTimeOffset? LastRefreshUtc => refresher?.LastRefreshUtc;

    /// <summary>Refreshes now. Concurrent callers share the one in flight rather than queueing a second.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            await gate.WaitAsync(cancellationToken);
            gate.Release();
            return;
        }

        try
        {
            refresher ??= new QuotaRefresher(tracker, () => httpClients.CreateClient(HttpClientName), options.Value);
            await refresher.RefreshAsync(Subjects(), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Lists every configured provider with its budget, without asking anybody — so the page shows them
    /// before the first refresh has finished, and a provider added since is not missing until the next.
    /// </summary>
    public void Declare()
    {
        foreach (var subject in Subjects())
        {
            tracker.Declare(subject.Subject, subject.Type, subject.Billing);
        }
    }

    /// <summary>Every enabled provider: each is something this hub may send a prompt to, so each is listed.</summary>
    public IReadOnlyList<QuotaSubject> Subjects() => providers.Configured
        .Select(route => new QuotaSubject(
            route.Id,
            route.Definition.NormalizedType(),
            route.Definition.ResolvedBaseUrl(),
            route.Definition.ApiKey,
            route.Definition.Billing,
            route.Definition.ResolvedAnthropicVersion(),
            route.Definition.NormalizedType() == ProviderDefinition.TypeChatGpt ? chatGpt?.Session(route.Id) : null))
        .ToArray();

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
                logger.LogWarning(ex, "Could not refresh the providers' limits");
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
}
