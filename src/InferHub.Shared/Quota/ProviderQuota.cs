using System.Text.Json.Serialization;

namespace InferHub.Shared.Quota;

/// <summary>
/// How close one cloud provider is to a limit, as far as this host can know (phase 106): the plan's
/// windows (a ChatGPT sign-in's five-hour and weekly), the vendor's rate limits, the month's spend
/// against a budget, and whether the vendor last said "no more".
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is something a vendor said, and when it said it.</b> Nothing is estimated from
/// tokens this host counted: a price table in config is a second invoice that drifts from the real
/// one, and a percentage that is confidently wrong is worse than a dash (106 D2).
/// </para>
/// <para>
/// A host reports its own providers and nobody else's — the coordinator its <c>Providers:</c>, a node
/// its backend and engines — because each holds its own credential (103 D1) and each spends its own
/// limit. The hub shows them side by side and adds nothing up.
/// </para>
/// </remarks>
public sealed record ProviderQuota(
    /// <summary>The provider id on the coordinator; the engine name, or the backend type, on a node.</summary>
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("type")] string Type,
    /// <summary>One of <see cref="QuotaLevel"/>.</summary>
    [property: JsonPropertyName("level")] string Level,
    /// <summary>The ChatGPT plan (<c>plus</c>, <c>pro</c>…) when the vendor named it.</summary>
    [property: JsonPropertyName("plan")] string? Plan,
    [property: JsonPropertyName("windows")] IReadOnlyList<QuotaWindow> Windows,
    [property: JsonPropertyName("billing")] QuotaBilling? Billing,
    /// <summary>The vendor's last refusal for a limit, as a sentence. Cleared by the next success.</summary>
    [property: JsonPropertyName("limitReached")] string? LimitReached,
    [property: JsonPropertyName("limitReachedAtUtc")] DateTimeOffset? LimitReachedAtUtc,
    /// <summary>When the vendor said to try again (<c>Retry-After</c>), if it did.</summary>
    [property: JsonPropertyName("retryAtUtc")] DateTimeOffset? RetryAtUtc,
    /// <summary>Why something is missing — no spend API for this credential, a usage endpoint that refused.</summary>
    [property: JsonPropertyName("note")] string? Note,
    /// <summary>The last time anything at all was learned about this provider.</summary>
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset? ObservedAtUtc);

/// <summary>One window a vendor counts in, and how much of it is gone.</summary>
public sealed record QuotaWindow(
    /// <summary><c>5h</c>, <c>weekly</c>, <c>requests</c>, <c>tokens</c>… — the window's own length when the vendor gave one.</summary>
    [property: JsonPropertyName("name")] string Name,
    /// <summary><see cref="QuotaWindow.Plan"/> or <see cref="QuotaWindow.Rate"/>.</summary>
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("usedPercent")] double UsedPercent,
    [property: JsonPropertyName("windowMinutes")] int? WindowMinutes,
    [property: JsonPropertyName("resetsAtUtc")] DateTimeOffset? ResetsAtUtc,
    /// <summary><see cref="QuotaWindow.FromHeaders"/> or <see cref="QuotaWindow.FromUsageEndpoint"/>.</summary>
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc,
    [property: JsonPropertyName("limit")] long? Limit = null,
    [property: JsonPropertyName("remaining")] long? Remaining = null)
{
    /// <summary>A subscription's allowance — ChatGPT's five-hour and weekly windows.</summary>
    public const string Plan = "plan";

    /// <summary>A per-minute rate limit on an API key.</summary>
    public const string Rate = "rate";

    /// <summary>Read off a real response's headers.</summary>
    public const string FromHeaders = "headers";

    /// <summary>Asked for on the refresh loop.</summary>
    public const string FromUsageEndpoint = "usage-endpoint";
}

/// <summary>This month's spend against a budget.</summary>
public sealed record QuotaBilling(
    /// <summary><c>2026-10</c> for a calendar month (UTC); the vendor's own word for a key's limit period otherwise.</summary>
    [property: JsonPropertyName("period")] string Period,
    /// <summary>Null when the vendor reports no spend to the credential this host holds.</summary>
    [property: JsonPropertyName("spentUsd")] decimal? SpentUsd,
    [property: JsonPropertyName("budgetUsd")] decimal? BudgetUsd,
    [property: JsonPropertyName("usedPercent")] double? UsedPercent,
    /// <summary><c>config</c> (<c>Billing:MonthlyBudgetUsd</c>) or <c>vendor</c> (a limit set on the key itself).</summary>
    [property: JsonPropertyName("budgetSource")] string? BudgetSource,
    /// <summary>Which vendor API the spend came from; null when none could be asked.</summary>
    [property: JsonPropertyName("spendSource")] string? SpendSource,
    [property: JsonPropertyName("warnPercent")] int WarnPercent,
    [property: JsonPropertyName("atUtc")] DateTimeOffset? AtUtc,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>What a node says about its own providers' limits (phase 106) — the 44 D6 mailbox again.</summary>
public sealed record NodeQuotaState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    [property: JsonPropertyName("providers")] IReadOnlyList<ProviderQuota> Providers,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

/// <summary>The values of <see cref="ProviderQuota.Level"/>.</summary>
public static class QuotaLevel
{
    /// <summary>Nothing known yet: no traffic, no usage endpoint, no budget.</summary>
    public const string Unknown = "unknown";

    public const string Ok = "ok";

    /// <summary>Some window or the budget is at or past its warning percentage.</summary>
    public const string Warn = "warn";

    /// <summary>A window or the budget is used up, or the vendor refused for a limit.</summary>
    public const string Limit = "limit";
}
