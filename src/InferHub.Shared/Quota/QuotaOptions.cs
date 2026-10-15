namespace InferHub.Shared.Quota;

/// <summary>
/// <c>Quota:</c> on either host (phase 106). Plain, like every options class here (rule 2); each host
/// binds it itself.
/// </summary>
public sealed class QuotaOptions
{
    public const string SectionName = "Quota";

    /// <summary>
    /// The undocumented endpoint OpenAI's own Codex client reads a ChatGPT plan's windows from (106 D3).
    /// </summary>
    public const string DefaultChatGptUsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    /// <summary>Off is off: no probe is sent, and windows come only from real responses' headers.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the spend and the usage endpoint are asked. Headers are read on every response regardless.</summary>
    public int RefreshSeconds { get; set; } = 300;

    /// <summary>
    /// Where a ChatGPT sign-in's five-hour and weekly windows are asked for. Empty turns the probe off,
    /// leaving only the headers a real request brings back.
    /// </summary>
    public string? ChatGptUsageUrl { get; set; } = DefaultChatGptUsageUrl;

    /// <summary>The percentage at which a plan or rate window turns the provider to <c>warn</c>.</summary>
    public int WarnPercent { get; set; } = 80;

    public TimeSpan RefreshInterval() => TimeSpan.FromSeconds(Math.Clamp(RefreshSeconds, 30, 86_400));
}

/// <summary>
/// <c>Billing:</c> on one provider (hub), on <c>Upstream:</c> or on one engine (node). Every field is
/// optional; a provider without it is shown with whatever its vendor reports on its own.
/// </summary>
public sealed class QuotaBillingOptions
{
    /// <summary>The month's budget in US dollars. The vendor's own cap, where it has one, is not read from here.</summary>
    public decimal? MonthlyBudgetUsd { get; set; }

    /// <summary>
    /// An <b>admin</b> key that may read the organisation's costs — OpenAI's <c>sk-admin-…</c>, Anthropic's
    /// <c>sk-ant-admin…</c>. Environment or user-secrets only, like every key. Never sent anywhere but the
    /// cost endpoint of the vendor the provider already talks to.
    /// </summary>
    public string? AdminApiKey { get; set; }

    /// <summary>The percentage of the budget at which the provider turns to <c>warn</c>.</summary>
    public int WarnPercent { get; set; } = 80;

    public bool HasBudget => MonthlyBudgetUsd is > 0;
}
