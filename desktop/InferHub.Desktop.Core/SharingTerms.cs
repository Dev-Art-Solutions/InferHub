using System.Globalization;
using System.Text.Json.Nodes;

namespace InferHub.Desktop;

/// <summary>
/// Phase 116 in the app: the owner's terms as the Overview tab edits them — sharing hours, one line each, and the
/// day's network allowance — and the sentence the tab shows for them. The node parses and checks the lines; the app
/// only splits what the owner typed.
/// </summary>
public static class SharingTerms
{
    /// <summary>One line per window; blank lines and a trailing <c>;</c>-separated list are both accepted.</summary>
    public static string[] Lines(string? text) =>
        (text ?? "")
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    /// <summary>The hours from a status's <c>sharing</c> block, one per line, for the editor.</summary>
    public static string HoursText(JsonNode? sharing) =>
        sharing?["hours"] is JsonArray hours
            ? string.Join(Environment.NewLine, hours.Select(h => h?.GetValue<string>()).Where(h => !string.IsNullOrWhiteSpace(h)))
            : "";

    /// <summary>"Any time" or the hours in a line; then today's allowance, if there is one.</summary>
    public static string Describe(JsonNode? sharing, TimeZoneInfo? viewer = null)
    {
        var hours = sharing?["hours"] is JsonArray { Count: > 0 } list
            ? "Shares " + string.Join("; ", list.Select(h => h?.GetValue<string>())) + $" ({sharing?["zone"]?.GetValue<string>()})"
            : "Shares at any hour";

        var allowance = sharing?["allowance"];
        var minutes = allowance?["minutes"]?.GetValue<int>() ?? 0;

        if (minutes <= 0)
        {
            return hours + " · no daily allowance.";
        }

        var used = allowance?["usedSeconds"]?.GetValue<double>() ?? 0;
        var resets = allowance?["resetsAtUtc"]?.GetValue<DateTimeOffset>();
        var at = resets is { } r ? TimeZoneInfo.ConvertTime(r, viewer ?? TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture) : "midnight";
        return $"{hours} · today {Math.Floor(used / 60):0} of {minutes} min of network work used, renewed at {at}.";
    }
}
