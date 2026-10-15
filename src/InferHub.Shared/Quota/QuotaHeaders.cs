using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace InferHub.Shared.Quota;

/// <summary>
/// The limits a vendor states on every response (phase 106), read off the headers and nothing else.
/// </summary>
/// <remarks>
/// Three spellings, one shape: Codex's <c>x-codex-primary-*</c> / <c>x-codex-secondary-*</c> (a ChatGPT
/// plan's short and weekly windows), OpenAI's <c>x-ratelimit-*</c> (also OpenAI-compatible servers that
/// copy it) and Anthropic's <c>anthropic-ratelimit-*</c>. A response carrying none of them yields
/// nothing, which is the normal case for Gemini and for a local server.
/// </remarks>
public static class QuotaHeaders
{
    public static IReadOnlyList<QuotaWindow> Read(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var windows = new List<QuotaWindow>();

        foreach (var which in (string[])["primary", "secondary"])
        {
            if (Double(headers, $"x-codex-{which}-used-percent") is not { } used)
            {
                continue;
            }

            var minutes = Int(headers, $"x-codex-{which}-window-minutes");
            var resets = Long(headers, $"x-codex-{which}-reset-at") is { } at
                ? DateTimeOffset.FromUnixTimeSeconds(at)
                : Long(headers, $"x-codex-{which}-reset-after-seconds") is { } after
                    ? now.AddSeconds(after)
                    : (DateTimeOffset?)null;

            windows.Add(new QuotaWindow(
                minutes is { } m ? WindowName(m) : which,
                QuotaWindow.Plan,
                Clamp(used),
                minutes,
                resets,
                QuotaWindow.FromHeaders,
                now));
        }

        foreach (var unit in (string[])["requests", "tokens"])
        {
            if (Rate(unit, Long(headers, $"x-ratelimit-limit-{unit}"), Long(headers, $"x-ratelimit-remaining-{unit}"),
                    Duration(Text(headers, $"x-ratelimit-reset-{unit}")) is { } after ? now + after : null, now) is { } window)
            {
                windows.Add(window);
            }
        }

        foreach (var unit in (string[])["requests", "tokens", "input-tokens", "output-tokens"])
        {
            if (Rate(unit, Long(headers, $"anthropic-ratelimit-{unit}-limit"), Long(headers, $"anthropic-ratelimit-{unit}-remaining"),
                    Instant(Text(headers, $"anthropic-ratelimit-{unit}-reset")), now) is { } window)
            {
                windows.Add(window);
            }
        }

        return windows;
    }

    /// <summary><c>Retry-After</c> as an instant: seconds, or an HTTP date.</summary>
    public static DateTimeOffset? RetryAt(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (headers.RetryAfter is not { } retry)
        {
            return null;
        }

        return retry.Delta is { } delta ? now + delta : retry.Date;
    }

    /// <summary>
    /// The vendor's own word for a refusal: <c>error.code</c>, else <c>error.status</c> (Gemini), else
    /// <c>error.type</c> (Anthropic), and <c>error.message</c>. Never more than a short sentence.
    /// </summary>
    public static (string? Code, string? Message) Error(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var code = Word(error, "code") ?? Word(error, "status") ?? Word(error, "type");
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (code, message is { Length: > 240 } ? message[..240] + "…" : message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary><c>300</c> → <c>5h</c>, <c>10080</c> → <c>weekly</c>: the names the vendor's own settings page uses.</summary>
    public static string WindowName(int minutes) => minutes switch
    {
        10_080 => "weekly",
        1_440 => "daily",
        _ when minutes % 1_440 == 0 => $"{minutes / 1_440}d",
        _ when minutes % 60 == 0 => $"{minutes / 60}h",
        _ => $"{minutes}m"
    };

    /// <summary>OpenAI's reset durations: <c>1s</c>, <c>6m0s</c>, <c>20ms</c>, <c>1h2m3.5s</c>.</summary>
    public static TimeSpan? Duration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var total = 0.0;
        var number = 0;
        var s = text.Trim();
        var parsedAny = false;

        for (var i = 0; i < s.Length;)
        {
            number = i;

            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
            {
                i++;
            }

            if (i == number || !double.TryParse(s[number..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            var unitStart = i;

            while (i < s.Length && char.IsLetter(s[i]))
            {
                i++;
            }

            total += s[unitStart..i] switch
            {
                "h" => value * 3600,
                "m" => value * 60,
                "s" or "" => value,
                "ms" => value / 1000,
                _ => double.NaN
            };

            if (double.IsNaN(total))
            {
                return null;
            }

            parsedAny = true;
        }

        return parsedAny ? TimeSpan.FromSeconds(total) : null;
    }

    private static QuotaWindow? Rate(string unit, long? limit, long? remaining, DateTimeOffset? resets, DateTimeOffset now)
    {
        if (limit is not > 0 || remaining is null)
        {
            return null;
        }

        return new QuotaWindow(
            unit,
            QuotaWindow.Rate,
            Clamp(100.0 * (limit.Value - remaining.Value) / limit.Value),
            null,
            resets,
            QuotaWindow.FromHeaders,
            now,
            limit,
            remaining);
    }

    private static double Clamp(double percent) => Math.Round(Math.Clamp(percent, 0, 100), 1);

    private static string? Word(JsonElement error, string name)
        => error.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static string? Text(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault()?.Trim() : null;

    private static double? Double(HttpResponseHeaders headers, string name)
        => double.TryParse(Text(headers, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? Long(HttpResponseHeaders headers, string name)
        => Text(headers, name) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? (long)value
            : null;

    private static int? Int(HttpResponseHeaders headers, string name) => Long(headers, name) is { } value ? (int)value : null;

    private static DateTimeOffset? Instant(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}
