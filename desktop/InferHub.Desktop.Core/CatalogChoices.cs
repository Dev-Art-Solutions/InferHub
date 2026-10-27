using System.Globalization;
using System.Text.Json.Nodes;

namespace InferHub.Desktop;

/// <summary>
/// Phase 119 in the app: the "Browse models" list — the node's <c>/fit/catalog</c> answer turned into rows, fitting
/// first as the node ordered them. The node classes each model; this only chooses the words.
/// </summary>
public static class CatalogChoices
{
    public sealed record Size(string Name, string Link, string Class, string Label);

    public sealed record Row(
        string Source,
        string Name,
        string Link,
        string Class,
        string Badge,
        string Headline,
        string Details,
        string Why,
        IReadOnlyList<Size> Sizes);

    public static IReadOnlyList<string> Sources => ["all", "huggingface", "ollama"];

    public static IReadOnlyList<string> Sorts => ["trending", "downloads", "newest"];

    public static IReadOnlyList<string> Kinds => ["text", "vision"];

    /// <summary>The class in a few words the owner reads at a glance.</summary>
    public static string Badge(string? fitClass) => fitClass switch
    {
        "card" => "fits on the card",
        "card-and-ram" => "card + RAM",
        "ram" => "fits in RAM (CPU)",
        "too-big" => "too big here",
        _ => "size unknown"
    };

    public static IReadOnlyList<Row> Rows(JsonObject reply)
    {
        var rows = new List<Row>();

        foreach (var entry in reply["entries"] as JsonArray ?? [])
        {
            if (entry is not JsonObject e)
            {
                continue;
            }

            var fit = e["fit"] as JsonObject;
            var cls = Text(fit?["class"]) ?? "unknown";
            var sizes = (e["sizes"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(s => new Size(
                    Text(s["name"]) ?? "",
                    Text(s["link"]) ?? "",
                    Text(s["fit"]?["class"]) ?? "unknown",
                    $"{Text(s["name"])} — {Badge(Text(s["fit"]?["class"]))}{(Number(s["fit"]?["weightsMiB"]) is { } mib ? $", {Gib(mib)}" : "")}"))
                .ToArray();
            var link = Text(e["link"]) ?? Text(e["name"]) ?? "";
            var weights = Number(fit?["weightsMiB"]) is { } w ? $" · {Gib(w)} of weights" : "";

            rows.Add(new Row(
                Text(e["source"]) ?? "",
                Text(e["name"]) ?? "",
                link,
                cls,
                Badge(cls),
                $"{Text(e["name"])}  [{Badge(cls)}{weights}]",
                Details(e),
                Text(fit?["why"]) ?? "",
                sizes));
        }

        return rows;
    }

    /// <summary>Each source in a line when it did not answer — the list still shows the other.</summary>
    public static IReadOnlyList<string> Problems(JsonObject reply) =>
        (reply["sources"] as JsonArray ?? [])
        .OfType<JsonObject>()
        .Where(s => s["ok"]?.GetValue<bool>() != true)
        .Select(s => $"{Title(Text(s["name"]))}: {Text(s["error"])}")
        .ToArray();

    public static string Title(string? source) => source switch
    {
        "huggingface" => "Hugging Face",
        "ollama" => "Ollama's library",
        _ => source ?? ""
    };

    private static string Details(JsonObject e)
    {
        var parts = new List<string> { Title(Text(e["source"])) };

        if (Number(e["downloads"]) is { } downloads)
        {
            parts.Add($"{Count(downloads)} {(Text(e["source"]) == "ollama" ? "pulls" : "downloads")}");
        }

        if (Number(e["likes"]) is { } likes)
        {
            parts.Add($"{Count(likes)} likes");
        }

        if (Text(e["updatedText"]) is { } updated)
        {
            parts.Add("updated " + updated);
        }
        else if (Text(e["updated"]) is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            parts.Add(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (e["capabilities"] is JsonArray { Count: > 0 } caps)
        {
            parts.Add(string.Join(", ", caps.Select(c => Text(c))));
        }

        if (e["moe"]?.GetValue<bool>() == true)
        {
            parts.Add("mixture of experts");
        }

        if (e["strata"]?.GetValue<bool>() == true)
        {
            parts.Add("a Strata repository");
        }

        if (Text(e["description"]) is { Length: > 0 } description)
        {
            parts.Add(description.Length > 160 ? description[..157] + "…" : description);
        }

        return string.Join(" · ", parts);
    }

    public static string Count(long n) => n switch
    {
        >= 1_000_000 => (n / 1e6).ToString(n >= 10_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1e3).ToString(n >= 10_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString(CultureInfo.InvariantCulture)
    };

    private static string Gib(long mib) => mib >= 1024
        ? (mib / 1024.0).ToString(mib >= 10 * 1024 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GiB"
        : mib.ToString(CultureInfo.InvariantCulture) + " MiB";

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
}
