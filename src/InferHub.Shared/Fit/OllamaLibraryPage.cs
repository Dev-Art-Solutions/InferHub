using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace InferHub.Shared.Fit;

/// <summary>
/// Ollama's library as its web page lists it (119 D2) — there is no documented API, and <c>ollama.com/api/tags</c> is
/// Ollama's cloud models. Read by hand: one <c>/library/&lt;name&gt;</c> link per model, its description, capability
/// badges, size badges, pulls and the date it was updated.
/// </summary>
public static partial class OllamaLibraryPage
{
    public sealed record Model(
        string Name,
        string? Description,
        IReadOnlyList<string> Capabilities,
        IReadOnlyList<string> Sizes,
        long? Pulls,
        DateTimeOffset? Updated,
        string? UpdatedText)
    {
        /// <summary>Only on Ollama's servers: a <c>cloud</c> badge and no size to pull.</summary>
        public bool CloudOnly => Sizes.Count == 0 && Capabilities.Contains("cloud");
    }

    [GeneratedRegex("""<a\s+href="/library/([^"/?#]+)"\s+class="group""", RegexOptions.IgnoreCase)]
    private static partial Regex Entry();

    [GeneratedRegex("""<p\s+class="[^"]*max-w-lg[^"]*"[^>]*>(.*?)</p>""", RegexOptions.Singleline)]
    private static partial Regex Description();

    // Capabilities are indigo badges, "cloud" a cyan one; sizes are the light-blue ones.
    [GeneratedRegex("""<span[^>]*class="[^"]*\bbg-(?:indigo|cyan)-50\b[^"]*"[^>]*>\s*([^<]+?)\s*</span>""")]
    private static partial Regex Capability();

    [GeneratedRegex("""<span[^>]*class="[^"]*bg-\[#ddf4ff\][^"]*"[^>]*>\s*([^<]+?)\s*</span>""")]
    private static partial Regex Size();

    [GeneratedRegex("""<span\s*>\s*([0-9.,]+[KMB]?)\s*</span>\s*<span[^>]*>&nbsp;Pulls""", RegexOptions.IgnoreCase)]
    private static partial Regex Pulls();

    [GeneratedRegex("""title="([^"]+)">\s*<span[^>]*>Updated&nbsp;</span>\s*<span\s*>([^<]+)</span>""")]
    private static partial Regex UpdatedAt();

    // 7b · 0.5b · e2b (Gemma's effective size) · 8x7b (a mixture of eight) · 335m.
    [GeneratedRegex(@"^(?:(\d+)x)?e?(\d+(?:\.\d+)?)([kmbt])$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeTag();

    public static IReadOnlyList<Model> Parse(string html)
    {
        var matches = Entry().Matches(html);
        var models = new List<Model>(matches.Count);

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
            var block = html[start..end];
            var name = WebUtility.HtmlDecode(matches[i].Groups[1].Value);

            if (models.Any(m => m.Name == name))
            {
                continue;
            }

            var description = Description().Match(block) is { Success: true } d ? Clean(d.Groups[1].Value) : null;
            var capabilities = Capability().Matches(block).Select(m => Clean(m.Groups[1].Value).ToLowerInvariant()).Distinct().ToArray();
            var sizes = Size().Matches(block).Select(m => Clean(m.Groups[1].Value).ToLowerInvariant()).Distinct().ToArray();
            var pulls = Pulls().Match(block) is { Success: true } p ? Count(p.Groups[1].Value) : null;
            DateTimeOffset? updated = null;
            string? updatedText = null;

            if (UpdatedAt().Match(block) is { Success: true } u)
            {
                updatedText = Clean(u.Groups[2].Value);

                if (DateTimeOffset.TryParseExact(u.Groups[1].Value.Replace(" UTC", "", StringComparison.Ordinal), "MMM d, yyyy h:mm tt",
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    updated = at;
                }
            }

            models.Add(new Model(name, description, capabilities, sizes, pulls, updated, updatedText));
        }

        return models;
    }

    /// <summary><c>7b</c> → 7 000 000 000; <c>8x7b</c> → 56 000 000 000; null for a tag that is not a size.</summary>
    public static long? Parameters(string size)
    {
        var match = SizeTag().Match(size.Trim());

        if (!match.Success)
        {
            return null;
        }

        var times = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        var unit = char.ToLowerInvariant(match.Groups[3].Value[0]) switch { 'k' => 1e3, 'm' => 1e6, 'b' => 1e9, _ => 1e12 };
        return (long)(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * unit * times);
    }

    /// <summary>The page for a sort and a search: <c>library?sort=popular&amp;q=qwen</c>.</summary>
    public static string Query(string sort, string? search)
    {
        var order = sort == CatalogSorts.Newest ? "newest" : "popular";
        return $"library?sort={order}" + (string.IsNullOrWhiteSpace(search) ? "" : "&q=" + Uri.EscapeDataString(search.Trim()));
    }

    internal static long? Count(string text)
    {
        text = text.Replace(",", "", StringComparison.Ordinal);
        var unit = char.ToUpperInvariant(text[^1]) switch { 'K' => 1e3, 'M' => 1e6, 'B' => 1e9, _ => 1 };
        var number = unit > 1 ? text[..^1] : text;
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? (long)(n * unit) : null;
    }

    private static string Clean(string text) => WebUtility.HtmlDecode(text).Trim();
}
