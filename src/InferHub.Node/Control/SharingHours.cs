using System.Globalization;

namespace InferHub.Node.Control;

/// <summary>
/// <c>Node:Sharing:Hours</c> (phase 116): when this node shares, as lines its owner can write by hand —
/// <c>"mon-fri 18:00-08:00"</c>, <c>"sat,sun all day"</c>, <c>"daily 22:00-07:00"</c>. Outside every line, the node
/// takes no new network jobs. No lines: always open, which is v3.76 exactly.
/// </summary>
/// <remarks>
/// <para>
/// <b>A window belongs to the day it starts on</b> (116 D2). <c>"fri 22:00-06:00"</c> is Friday night into Saturday
/// morning; an end at or before the start crosses midnight. Windows that touch or overlap are one: <c>"daily
/// 22:00-24:00"</c> and <c>"daily 00:00-07:00"</c> never close at midnight.
/// </para>
/// <para>
/// <b>The owner's clock, not the server's</b>: times are in <c>Node:Sharing:TimeZone</c> (IANA or Windows id),
/// the machine's own zone when unset. A start that falls in a spring-forward gap opens when the clock reaches the
/// next valid minute.
/// </para>
/// </remarks>
public sealed class SharingHours
{
    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private readonly IReadOnlyList<Window> windows;

    private SharingHours(IReadOnlyList<Window> windows, TimeZoneInfo zone)
    {
        this.windows = windows;
        Zone = zone;
    }

    public static SharingHours Always { get; } = new([], TimeZoneInfo.Local);

    public TimeZoneInfo Zone { get; }

    /// <summary>True when there are no lines: the hours never close.</summary>
    public bool IsAlways => windows.Count == 0;

    /// <summary>Parses the lines and the zone; the first problem as a sentence, and <see cref="Always"/>, when one does not.</summary>
    public static SharingHours Parse(IEnumerable<string>? lines, string? timeZone, out string? error)
    {
        error = null;
        TimeZoneInfo zone;

        if (string.IsNullOrWhiteSpace(timeZone))
        {
            zone = TimeZoneInfo.Local;
        }
        else if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out zone!))
        {
            error = $"'{timeZone}' is not a time zone this machine knows (an IANA id like Europe/Sofia, or a Windows id).";
            return Always;
        }

        var parsed = new List<Window>();

        foreach (var raw in lines ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (ParseLine(raw.Trim(), out var window, out var problem))
            {
                parsed.Add(window);
                continue;
            }

            error = $"'{raw.Trim()}': {problem} Write it as \"mon-fri 18:00-08:00\", \"sat,sun all day\" or \"daily 22:00-07:00\".";
            return Always;
        }

        return new SharingHours(parsed, zone);
    }

    /// <summary>
    /// Whether the hours are open at <paramref name="utc"/>, and when that next changes — the end of the open stretch,
    /// or the start of the next one. Null: it never changes (no lines, or a stretch that covers the whole week).
    /// </summary>
    public bool IsOpen(DateTimeOffset utc, out DateTimeOffset? changesAt)
    {
        changesAt = null;

        if (windows.Count == 0)
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTime(utc, Zone).DateTime.Date;
        var spans = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        // A window starting yesterday may still be open; the next opening is within a week and a day.
        for (var offset = -1; offset <= 8; offset++)
        {
            var day = local.AddDays(offset);

            foreach (var window in windows)
            {
                if (!window.Days.Contains(day.DayOfWeek))
                {
                    continue;
                }

                var start = day + window.From;
                var end = window.To <= window.From ? day.AddDays(1) + window.To : day + window.To;
                spans.Add((ToUtc(start), ToUtc(end)));
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        foreach (var span in spans.Where(s => s.End > s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, span.End > merged[^1].End ? span.End : merged[^1].End);
            }
            else
            {
                merged.Add(span);
            }
        }

        foreach (var span in merged)
        {
            if (span.Start <= utc && utc < span.End)
            {
                // A stretch running past the horizon is a week with no gap in it.
                changesAt = span == merged[^1] && span.End - utc > TimeSpan.FromDays(7) ? null : span.End;
                return true;
            }

            if (span.Start > utc)
            {
                changesAt = span.Start;
                return false;
            }
        }

        return false;
    }

    /// <summary>"Sat 22:00" in the owner's zone — how a reason names a moment.</summary>
    public string Describe(DateTimeOffset utc, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(utc, Zone);
        var today = TimeZoneInfo.ConvertTime(now, Zone).Date;
        var day = local.Date == today ? "today" : local.Date == today.AddDays(1) ? "tomorrow" : local.ToString("ddd", CultureInfo.InvariantCulture);
        return $"{day} {local:HH:mm}";
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A minute the clock skips (spring forward) opens at the first one it shows.
        while (Zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone), TimeSpan.Zero);
    }

    private static bool ParseLine(string line, out Window window, out string? problem)
    {
        window = default;
        problem = null;
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 2)
        {
            problem = "a line is days, then hours.";
            return false;
        }

        if (!ParseDays(parts[0], out var days, out problem))
        {
            return false;
        }

        var hours = parts[1].ToLowerInvariant();

        if (hours is "all day" or "all-day")
        {
            window = new Window(days, TimeSpan.Zero, TimeSpan.FromDays(1));
            return true;
        }

        var range = hours.Split('-', StringSplitOptions.TrimEntries);

        if (range.Length != 2 || !ParseTime(range[0], allowMidnightEnd: false, out var from) || !ParseTime(range[1], allowMidnightEnd: true, out var to))
        {
            problem = "the hours are HH:mm-HH:mm (24:00 for midnight at the end), or \"all day\".";
            return false;
        }

        if (from == to)
        {
            problem = "a window that starts and ends at the same minute is either nothing or a whole day — write \"all day\" for the second.";
            return false;
        }

        // "18:00-24:00" keeps 24:00: an end after the start is the same day's, and the same day's 24:00 is midnight.
        window = new Window(days, from, to);
        return true;
    }

    private static bool ParseDays(string text, out HashSet<DayOfWeek> days, out string? problem)
    {
        days = [];
        problem = null;

        switch (text.ToLowerInvariant())
        {
            case "daily" or "every" or "everyday":
                days = [.. Enum.GetValues<DayOfWeek>()];
                return true;
            case "weekdays":
                days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
                return true;
            case "weekends":
                days = [DayOfWeek.Saturday, DayOfWeek.Sunday];
                return true;
        }

        foreach (var item in text.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = item.Split('-', StringSplitOptions.TrimEntries);

            if (ends.Length is < 1 or > 2 || !TryDay(ends[0], out var first) || !TryDay(ends[^1], out var last))
            {
                problem = $"'{item}' is not a day (mon, tue, … sun, a range like mon-fri, or daily, weekdays, weekends).";
                return false;
            }

            // mon-fri, and fri-mon wrapping through the weekend.
            for (var d = first; ; d = (d + 1) % 7)
            {
                days.Add((DayOfWeek)d);

                if (d == last)
                {
                    break;
                }
            }
        }

        return days.Count > 0;
    }

    private static bool TryDay(string text, out int day)
    {
        day = Array.IndexOf(DayNames, text.Length >= 3 ? text[..3] : text);
        return day >= 0;
    }

    private static bool ParseTime(string text, bool allowMidnightEnd, out TimeSpan time)
    {
        time = default;

        if (allowMidnightEnd && text == "24:00")
        {
            time = TimeSpan.FromDays(1);
            return true;
        }

        return TimeSpan.TryParseExact(text, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
    }

    private readonly record struct Window(HashSet<DayOfWeek> Days, TimeSpan From, TimeSpan To);
}
