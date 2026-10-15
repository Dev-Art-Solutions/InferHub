using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace InferHub.Shared.Quota;

/// <summary>
/// The vendor endpoints the refresh loop asks (phase 106). Each takes an <see cref="HttpClient"/> with
/// no base address and no default headers, sends one credential to one vendor, and throws
/// <see cref="QuotaProbeException"/> with a sentence when the answer is not one it understands.
/// </summary>
public static class QuotaProbes
{
    public const string OpenAiCosts = "openai-costs";
    public const string AnthropicCostReport = "anthropic-cost-report";
    public const string OpenRouterKey = "openrouter-key";

    private const int MaxPages = 12;

    /// <summary>OpenAI's <c>GET /organization/costs</c>, summed since <paramref name="since"/>, in dollars. Admin key only.</summary>
    public static async Task<decimal> OpenAiCostsAsync(HttpClient http, string baseUrl, string adminKey, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var total = 0m;
        string? page = null;

        for (var i = 0; i < MaxPages; i++)
        {
            var url = $"{baseUrl.TrimEnd('/')}/organization/costs?start_time={since.ToUnixTimeSeconds()}&bucket_width=1d&limit=31"
                      + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);

            using var document = await SendAsync(http, request, "OpenAI's costs API", cancellationToken);

            foreach (var bucket in Array(document.RootElement, "data"))
            {
                foreach (var result in Array(bucket, "results"))
                {
                    if (result.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Object
                        && amount.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number)
                    {
                        total += value.GetDecimal();
                    }
                }
            }

            page = NextPage(document.RootElement);

            if (page is null)
            {
                break;
            }
        }

        return Math.Round(total, 4);
    }

    /// <summary>
    /// Anthropic's <c>GET /v1/organizations/cost_report</c>, summed since <paramref name="since"/>. The
    /// vendor answers in cents as a decimal string; this returns dollars. Admin key only.
    /// </summary>
    public static async Task<decimal> AnthropicCostAsync(HttpClient http, string baseUrl, string adminKey, string version, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var total = 0m;
        string? page = null;
        var root = baseUrl.TrimEnd('/');
        root = root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root : root + "/v1";

        for (var i = 0; i < MaxPages; i++)
        {
            var url = $"{root}/organizations/cost_report?starting_at={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}&bucket_width=1d&limit=31"
                      + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("x-api-key", adminKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", version);

            using var document = await SendAsync(http, request, "Anthropic's cost report", cancellationToken);

            foreach (var bucket in Array(document.RootElement, "data"))
            {
                foreach (var result in Array(bucket, "results"))
                {
                    if (result.TryGetProperty("amount", out var amount)
                        && decimal.TryParse(amount.ValueKind == JsonValueKind.String ? amount.GetString() : amount.GetRawText(),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var cents))
                    {
                        total += cents;
                    }
                }
            }

            page = NextPage(document.RootElement);

            if (page is null)
            {
                break;
            }
        }

        return Math.Round(total / 100m, 4);
    }

    /// <summary>What OpenRouter says about the key itself — its spend, and the limit set on it if any.</summary>
    public sealed record OpenRouterKeyState(decimal? UsageMonthly, decimal? Usage, decimal? Limit, decimal? LimitRemaining, string? LimitReset);

    /// <summary>OpenRouter's <c>GET /key</c>, with the inference key — the one vendor that tells any key its own spend.</summary>
    public static async Task<OpenRouterKeyState> OpenRouterKeyAsync(HttpClient http, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var document = await SendAsync(http, request, "OpenRouter's key endpoint", cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new QuotaProbeException("OpenRouter's key endpoint answered without a data object.");
        }

        return new OpenRouterKeyState(
            Number(data, "usage_monthly"),
            Number(data, "usage"),
            Number(data, "limit"),
            Number(data, "limit_remaining"),
            data.TryGetProperty("limit_reset", out var reset) && reset.ValueKind == JsonValueKind.String ? reset.GetString() : null);
    }

    /// <summary>What the ChatGPT usage endpoint said: the plan, and its windows.</summary>
    public sealed record ChatGptUsage(string? Plan, IReadOnlyList<QuotaWindow> Windows);

    /// <summary>
    /// The endpoint OpenAI's Codex client reads a plan's windows from (106 D3). Undocumented; a refusal
    /// is a <see cref="QuotaProbeException"/> naming the status, never a guess.
    /// </summary>
    public static async Task<ChatGptUsage> ChatGptUsageAsync(HttpClient http, string url, string accessToken, string? accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        using var document = await SendAsync(http, request, "the ChatGPT usage endpoint", cancellationToken);
        return ParseChatGptUsage(document.RootElement, now);
    }

    public static ChatGptUsage ParseChatGptUsage(JsonElement root, DateTimeOffset now)
    {
        var plan = root.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var windows = new List<QuotaWindow>();

        if (root.TryGetProperty("rate_limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            foreach (var (property, fallback) in (ReadOnlySpan<(string, string)>)[("primary_window", "primary"), ("secondary_window", "secondary")])
            {
                if (!limit.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object
                    || Number(window, "used_percent") is not { } used)
                {
                    continue;
                }

                var seconds = Number(window, "limit_window_seconds");
                int? minutes = seconds is { } s ? (int)(s / 60) : null;
                var resets = Number(window, "reset_at") is { } at
                    ? DateTimeOffset.FromUnixTimeSeconds((long)at)
                    : Number(window, "reset_after_seconds") is { } after
                        ? now.AddSeconds((double)after)
                        : (DateTimeOffset?)null;

                windows.Add(new QuotaWindow(
                    minutes is { } m ? QuotaHeaders.WindowName(m) : fallback,
                    QuotaWindow.Plan,
                    Math.Round(Math.Clamp((double)used, 0, 100), 1),
                    minutes,
                    resets,
                    QuotaWindow.FromUsageEndpoint,
                    now));
            }
        }

        if (plan is null && windows.Count == 0)
        {
            throw new QuotaProbeException("the ChatGPT usage endpoint answered with neither a plan nor a window.");
        }

        return new ChatGptUsage(plan, windows);
    }

    /// <summary>The first day of <paramref name="now"/>'s month, UTC — the period every budget here is for.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var (code, message) = QuotaHeaders.Error(body);
            throw new QuotaProbeException(
                $"{what} answered {(int)response.StatusCode}{(code is null ? "" : $" {code}")}{(message is null ? "" : $": {message}")}",
                (int)response.StatusCode);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new QuotaProbeException($"{what} answered {(int)response.StatusCode} with something that is not JSON.");
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static string? NextPage(JsonElement root)
        => root.TryGetProperty("next_page", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString())
           && !(root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.False)
            ? next.GetString()
            : null;

    private static decimal? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null
            }
            : null;
}

/// <summary>A probe's answer was not one it could read. The message is the sentence the page shows.</summary>
public sealed class QuotaProbeException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}
