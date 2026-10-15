using System.Collections.Concurrent;
using InferHub.Shared.ChatGpt;

namespace InferHub.Shared.Quota;

/// <summary>One provider the refresh loop knows how to ask about (phase 106).</summary>
/// <param name="Subject">The provider id (hub), or the engine name / backend type (node).</param>
/// <param name="Type">The hub's provider type or the node's backend type, lower-case.</param>
/// <param name="ChatGpt">This host's own session, for a <c>chatgpt</c> subject. Never another host's (103 D1).</param>
public sealed record QuotaSubject(
    string Subject,
    string Type,
    string? BaseUrl,
    string? ApiKey,
    QuotaBillingOptions? Billing,
    string? AnthropicVersion = null,
    ChatGptSession? ChatGpt = null);

/// <summary>
/// The periodic half (phase 106): the month's spend where the vendor reports it, OpenRouter's key, and a
/// ChatGPT plan's windows. One class composed by both hosts, which only differ in where the list of
/// subjects comes from.
/// </summary>
public sealed class QuotaRefresher(QuotaTracker tracker, Func<HttpClient> http, QuotaOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>A usage endpoint that refused is not asked again for an hour; it is not going to change its mind sooner.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> chatGptBackoff = new(StringComparer.OrdinalIgnoreCase);

    public static readonly TimeSpan ChatGptBackoff = TimeSpan.FromHours(1);

    public DateTimeOffset? LastRefreshUtc { get; private set; }

    public async Task RefreshAsync(IReadOnlyList<QuotaSubject> subjects, CancellationToken cancellationToken)
    {
        tracker.WarnPercent = options.WarnPercent is > 0 and <= 100 ? options.WarnPercent : 80;
        tracker.Retain(subjects.Select(s => s.Subject));

        foreach (var subject in subjects)
        {
            tracker.Declare(subject.Subject, subject.Type, subject.Billing);
        }

        if (options.Enabled)
        {
            await Task.WhenAll(subjects.Select(subject => RefreshOneAsync(subject, cancellationToken)));
        }

        LastRefreshUtc = clock.GetUtcNow();
    }

    private async Task RefreshOneAsync(QuotaSubject subject, CancellationToken cancellationToken)
    {
        try
        {
            switch (subject.Type)
            {
                case "chatgpt":
                    await ChatGptAsync(subject, cancellationToken);
                    break;
                case "openrouter":
                    await OpenRouterAsync(subject, cancellationToken);
                    break;
                case "anthropic" when !string.IsNullOrWhiteSpace(subject.Billing?.AdminApiKey):
                    await SpendAsync(subject, QuotaProbes.AnthropicCostReport, (client, since, ct) => QuotaProbes.AnthropicCostAsync(
                        client, subject.BaseUrl ?? Upstream.UpstreamDefaults.AnthropicBaseUrl, subject.Billing!.AdminApiKey!.Trim(),
                        subject.AnthropicVersion ?? Anthropic.AnthropicUpstreamClient.DefaultVersion, since, ct), cancellationToken);
                    break;
                case "openai" or "openai-compatible" when !string.IsNullOrWhiteSpace(subject.Billing?.AdminApiKey):
                    await SpendAsync(subject, QuotaProbes.OpenAiCosts, (client, since, ct) => QuotaProbes.OpenAiCostsAsync(
                        client, subject.BaseUrl ?? "https://api.openai.com/v1", subject.Billing!.AdminApiKey!.Trim(), since, ct), cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One provider's probe never stops another's, and never the host.
            tracker.SetNote(subject.Subject, ex is QuotaProbeException ? ex.Message : $"the limits could not be read: {ex.Message}");
        }
    }

    private async Task SpendAsync(
        QuotaSubject subject,
        string source,
        Func<HttpClient, DateTimeOffset, CancellationToken, Task<decimal>> probe,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var period = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            using var client = http();
            var spent = await probe(client, QuotaProbes.MonthStart(now), cancellationToken);
            tracker.SetBilling(subject.Subject, spent, source, period);
        }
        catch (QuotaProbeException ex)
        {
            tracker.SetBilling(subject.Subject, null, source, period, error: ex.Message);
        }
    }

    private async Task OpenRouterAsync(QuotaSubject subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subject.ApiKey))
        {
            return;
        }

        var period = clock.GetUtcNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            using var client = http();
            var key = await QuotaProbes.OpenRouterKeyAsync(client, subject.BaseUrl ?? Upstream.UpstreamDefaults.OpenRouterBaseUrl, subject.ApiKey.Trim(), cancellationToken);

            if (subject.Billing is { HasBudget: true })
            {
                // The operator's monthly budget is against this month's spend.
                tracker.SetBilling(subject.Subject, key.UsageMonthly ?? key.Usage, QuotaProbes.OpenRouterKey, period);
            }
            else if (key.Limit is > 0)
            {
                // The key's own limit, over the key's own period.
                var spent = key.LimitRemaining is { } remaining ? key.Limit.Value - remaining : key.Usage;
                tracker.SetBilling(subject.Subject, spent, QuotaProbes.OpenRouterKey, key.LimitReset ?? "key lifetime", key.Limit);
            }
            else
            {
                tracker.SetBilling(subject.Subject, key.UsageMonthly ?? key.Usage, QuotaProbes.OpenRouterKey, period);
            }
        }
        catch (QuotaProbeException ex)
        {
            tracker.SetBilling(subject.Subject, null, QuotaProbes.OpenRouterKey, period, error: ex.Message);
        }
    }

    private async Task ChatGptAsync(QuotaSubject subject, CancellationToken cancellationToken)
    {
        if (subject.ChatGpt is not { } session || string.IsNullOrWhiteSpace(options.ChatGptUsageUrl))
        {
            return;
        }

        var state = session.State();

        if (!state.SignedIn || !state.PlanUsage)
        {
            tracker.SetNote(subject.Subject, state.SignedIn ? "signed in without plan usage" : "not signed in");
            return;
        }

        var now = clock.GetUtcNow();

        if (chatGptBackoff.TryGetValue(subject.Subject, out var until) && until > now)
        {
            return;
        }

        string token;

        try
        {
            token = await session.AccessTokenAsync(cancellationToken);
        }
        catch (ChatGptUnavailableException)
        {
            // Paused for the usage limit, or the session ended: the tracker already shows the refusal.
            return;
        }

        try
        {
            using var client = http();
            var usage = await QuotaProbes.ChatGptUsageAsync(client, options.ChatGptUsageUrl!.Trim(), token, session.ActiveAccountId(), now, cancellationToken);
            chatGptBackoff.TryRemove(subject.Subject, out _);
            tracker.SetWindows(subject.Subject, usage.Windows, usage.Plan);
        }
        catch (QuotaProbeException ex)
        {
            chatGptBackoff[subject.Subject] = now + ChatGptBackoff;
            tracker.SetNote(subject.Subject,
                $"{ex.Message}. The five-hour and weekly windows will show from the headers of the next real request, if the vendor sends them; asked again in an hour.");
        }
    }
}
