using System.Collections.Concurrent;
using System.Net;

namespace InferHub.Shared.Quota;

/// <summary>
/// What this host knows about each of its cloud providers' limits (phase 106). In memory (rule 4): a
/// restart forgets it, and the next response or refresh relearns it — a lost window costs a dash on a
/// page, never a wrong answer.
/// </summary>
public sealed class QuotaTracker(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when anything a page would show changed. Subscribers throttle.</summary>
    public event Action? Changed;

    /// <summary>The percentage at which a plan or rate window warns (<c>Quota:WarnPercent</c>).</summary>
    public int WarnPercent { get; set; } = 80;

    /// <summary>
    /// A provider this host is configured with, so it is listed before it has served anything. The
    /// budget is the configured one; the spend arrives with the refresh.
    /// </summary>
    public void Declare(string subject, string type, QuotaBillingOptions? billing = null)
    {
        var entry = entries.GetOrAdd(subject, s => new Entry(s));

        lock (entry)
        {
            entry.Type = type;
            entry.Declared = true;
            entry.Budget = billing is { HasBudget: true } ? billing.MonthlyBudgetUsd : null;
            entry.BillingWarnPercent = billing?.WarnPercent is > 0 and <= 100 ? billing.WarnPercent : 80;
        }
    }

    /// <summary>Forgets declared providers that are no longer configured.</summary>
    public void Retain(IEnumerable<string> subjects)
    {
        var keep = new HashSet<string>(subjects, StringComparer.OrdinalIgnoreCase);

        foreach (var key in entries.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            if (entries.TryGetValue(key, out var entry) && entry.Declared)
            {
                entries.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// A real response from <paramref name="subject"/>'s vendor: its limit headers, and — for a 429 or a
    /// 402 — the refusal. A success clears the refusal, because the vendor has just said otherwise.
    /// </summary>
    public void Observe(string subject, HttpResponseMessage response, string? errorCode = null, string? errorMessage = null)
    {
        var now = clock.GetUtcNow();
        var windows = QuotaHeaders.Read(response.Headers, now);
        var status = (int)response.StatusCode;
        var refused = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired;

        if (windows.Count == 0 && !refused && !entries.ContainsKey(subject))
        {
            // A local server behind an OpenAI-typed backend says nothing about limits: not a provider to list.
            return;
        }

        var entry = entries.GetOrAdd(subject, s => new Entry(s));

        lock (entry)
        {
            Merge(entry, windows);
            entry.ObservedAt = now;

            if (refused)
            {
                entry.LimitReached = $"{status} {errorCode ?? response.ReasonPhrase ?? "limit"}"
                                     + (string.IsNullOrWhiteSpace(errorMessage) ? "" : $": {errorMessage}");
                entry.LimitReachedAt = now;
                entry.RetryAt = QuotaHeaders.RetryAt(response.Headers, now);
            }
            else if (response.IsSuccessStatusCode)
            {
                entry.LimitReached = null;
                entry.LimitReachedAt = null;
                entry.RetryAt = null;
            }
        }

        Changed?.Invoke();
    }

    public void SetWindows(string subject, IReadOnlyList<QuotaWindow> windows, string? plan)
    {
        var entry = entries.GetOrAdd(subject, s => new Entry(s));

        lock (entry)
        {
            Merge(entry, windows);
            entry.Plan = plan ?? entry.Plan;
            entry.ObservedAt = clock.GetUtcNow();
            entry.ProbeNote = null;
        }

        Changed?.Invoke();
    }

    public void SetBilling(string subject, decimal? spentUsd, string? spendSource, string period, decimal? vendorBudgetUsd = null, string? error = null)
    {
        var entry = entries.GetOrAdd(subject, s => new Entry(s));

        lock (entry)
        {
            entry.Spent = spentUsd;
            entry.SpendSource = spendSource;
            entry.Period = period;
            entry.VendorBudget = vendorBudgetUsd;
            entry.BillingError = error;
            entry.BillingAt = clock.GetUtcNow();
            entry.ObservedAt = spentUsd is null ? entry.ObservedAt : entry.BillingAt;
        }

        Changed?.Invoke();
    }

    /// <summary>Why a probe could not answer. Kept until one does.</summary>
    public void SetNote(string subject, string? note)
    {
        var entry = entries.GetOrAdd(subject, s => new Entry(s));

        lock (entry)
        {
            entry.ProbeNote = note;
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<ProviderQuota> Snapshot()
    {
        var now = clock.GetUtcNow();

        return entries.Values
            .Select(entry =>
            {
                lock (entry)
                {
                    return Build(entry, now);
                }
            })
            .OrderBy(quota => quota.Subject, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ProviderQuota? Of(string subject) => Snapshot().FirstOrDefault(q => string.Equals(q.Subject, subject, StringComparison.OrdinalIgnoreCase));

    private static void Merge(Entry entry, IReadOnlyList<QuotaWindow> windows)
    {
        foreach (var window in windows)
        {
            entry.Windows[window.Name] = window;
        }
    }

    private ProviderQuota Build(Entry entry, DateTimeOffset now)
    {
        // A window whose reset has passed has started again; the old number would be a false alarm.
        var windows = entry.Windows.Values
            .Select(w => w.ResetsAtUtc is { } reset && reset <= now ? w with { UsedPercent = 0, ResetsAtUtc = null } : w)
            .OrderBy(w => w.Kind == QuotaWindow.Plan ? 0 : 1)
            .ThenBy(w => w.WindowMinutes ?? int.MaxValue)
            .ThenBy(w => w.Name, StringComparer.Ordinal)
            .ToArray();

        var limitReached = entry.RetryAt is { } retry && retry <= now ? null : entry.LimitReached;
        var billing = Billing(entry, now);

        var notes = new List<string>();

        if (entry.ProbeNote is { } probe)
        {
            notes.Add(probe);
        }

        if (billing is { SpentUsd: null, BudgetUsd: not null, Error: null })
        {
            notes.Add(NoSpendNote(entry.Type));
        }

        var level = limitReached is not null
                    || windows.Any(w => w.UsedPercent >= 100)
                    || billing?.UsedPercent >= 100
            ? QuotaLevel.Limit
            : windows.Any(w => w.UsedPercent >= WarnPercent) || billing?.UsedPercent >= billing?.WarnPercent
                ? QuotaLevel.Warn
                : windows.Length > 0 || billing?.SpentUsd is not null
                    ? QuotaLevel.Ok
                    : QuotaLevel.Unknown;

        return new ProviderQuota(
            entry.Subject,
            entry.Type ?? "unknown",
            level,
            entry.Plan,
            windows,
            billing,
            limitReached,
            limitReached is null ? null : entry.LimitReachedAt,
            limitReached is null ? null : entry.RetryAt,
            notes.Count == 0 ? null : string.Join(" ", notes),
            entry.ObservedAt);
    }

    private static QuotaBilling? Billing(Entry entry, DateTimeOffset now)
    {
        var budget = entry.Budget ?? entry.VendorBudget;

        if (budget is null && entry.Spent is null && entry.BillingError is null)
        {
            return null;
        }

        var period = entry.Period ?? now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        return new QuotaBilling(
            period,
            entry.Spent,
            budget,
            budget is > 0 && entry.Spent is { } spent ? Math.Round((double)(spent / budget.Value * 100), 1) : null,
            entry.Budget is not null ? "config" : entry.VendorBudget is not null ? "vendor" : null,
            entry.SpendSource,
            entry.BillingWarnPercent,
            entry.BillingAt,
            entry.BillingError);
    }

    private static string NoSpendNote(string? type) => type switch
    {
        "gemini" => "Google reports no spend to an API key; the budget is a reminder, and a Cloud Billing cap shows here only as the 429 it causes.",
        "chatgpt" => "A ChatGPT plan has no spend; its windows are the limit.",
        _ => "This vendor reports no spend to this credential; set Billing:AdminApiKey (an OpenAI or Anthropic admin key) to read it."
    };

    private sealed class Entry(string subject)
    {
        public string Subject { get; } = subject;
        public string? Type { get; set; }
        public bool Declared { get; set; }
        public string? Plan { get; set; }
        public Dictionary<string, QuotaWindow> Windows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public decimal? Budget { get; set; }
        public decimal? VendorBudget { get; set; }
        public int BillingWarnPercent { get; set; } = 80;
        public decimal? Spent { get; set; }
        public string? SpendSource { get; set; }
        public string? Period { get; set; }
        public string? BillingError { get; set; }
        public DateTimeOffset? BillingAt { get; set; }
        public string? LimitReached { get; set; }
        public DateTimeOffset? LimitReachedAt { get; set; }
        public DateTimeOffset? RetryAt { get; set; }
        public string? ProbeNote { get; set; }
        public DateTimeOffset? ObservedAt { get; set; }
    }
}

/// <summary>
/// Which provider the HTTP call in flight belongs to (phase 106). Set around a dialect call by the one
/// place that knows — the hub's dispatcher, a node's backend — and read by
/// <see cref="QuotaObservingHandler"/> inside the pooled client, so no dialect learned a new parameter.
/// </summary>
public static class QuotaScope
{
    private static readonly AsyncLocal<string?> current = new();

    public static string? Current => current.Value;

    public static IDisposable Enter(string? subject)
    {
        var previous = current.Value;
        current.Value = subject;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}

/// <summary>
/// Reads every response's limit headers into the <see cref="QuotaTracker"/>. Observing never fails a
/// request: anything that goes wrong here is swallowed and the response goes on unchanged.
/// </summary>
public sealed class QuotaObservingHandler(QuotaTracker tracker) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (QuotaScope.Current is not { } subject)
        {
            return response;
        }

        try
        {
            string? code = null, message = null;

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.PaymentRequired)
            {
                // Buffered, so the dialect reads the same body after us. A refusal is a few hundred bytes.
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                (code, message) = QuotaHeaders.Error(await response.Content.ReadAsStringAsync(cancellationToken));
            }

            tracker.Observe(subject, response, code, message);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return response;
    }
}
