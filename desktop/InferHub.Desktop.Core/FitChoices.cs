using System.Globalization;
using System.Text.Json.Nodes;

namespace InferHub.Desktop;

/// <summary>
/// Phase 118 in the app: the "Check fit" dialog's words — the context the owner types (<c>32k</c>, <c>128K</c>,
/// <c>40960</c>), the quick picks, and the node's <c>/fit</c> answer turned into one row per engine, best first. The
/// node does the arithmetic; this only chooses what to show.
/// </summary>
public static class FitChoices
{
    public sealed record Row(
        string Engine,
        string Variant,
        bool Available,
        bool Fits,
        string Headline,
        IReadOnlyList<string> Why,
        IReadOnlyList<string> OtherVariants);

    /// <summary><c>32k</c> → 32768, <c>1M</c> → 1048576, <c>40960</c> → 40960; null for anything else.</summary>
    public static int? ParseContext(string? text)
    {
        var value = (text ?? string.Empty).Trim().Replace("_", "").Replace(",", "").Replace(" ", "");

        if (value.Length == 0)
        {
            return null;
        }

        var unit = char.ToLowerInvariant(value[^1]) switch { 'k' => 1024, 'm' => 1024 * 1024, _ => 1 };
        var number = unit == 1 ? value : value[..^1];

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 && n * unit <= 16 * 1024 * 1024
            ? (int)Math.Round(n * unit)
            : null;
    }

    public static string FormatContext(int tokens) =>
        tokens >= 1024 * 1024 && tokens % (1024 * 1024) == 0 ? $"{tokens / (1024 * 1024)}M"
        : tokens >= 1024 && tokens % 1024 == 0 ? $"{tokens / 1024}K"
        : tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Quick picks up to the model's trained context (and that context itself), when it is known.</summary>
    public static IReadOnlyList<int> Picks(int? trained)
    {
        var picks = new List<int> { 4096, 8192, 16384, 32768, 65536, 131072, 262144 };

        if (trained is { } max)
        {
            picks.RemoveAll(p => p > max);

            if (!picks.Contains(max))
            {
                picks.Add(max);
            }
        }

        return picks;
    }

    /// <summary>One row per engine — the node's best option for it — then the engines that cannot load the model.</summary>
    public static IReadOnlyList<Row> Rows(JsonObject answer)
    {
        var options = (answer["options"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var rows = new List<Row>();

        foreach (var group in options.GroupBy(o => S(o["engine"])))
        {
            var best = group.First();
            var others = group.Skip(1).Where(o => B(o["fits"])).Select(o => S(o["variant"])).Take(6).ToArray();
            rows.Add(new Row(group.Key, S(best["variant"]), B(best["available"]), B(best["fits"]), Headline(best),
                (best["why"] as JsonArray ?? []).Select(w => S(w)).ToArray(), others));
        }

        return rows;
    }

    /// <summary>The variant's own line, for an owner who picks another size of the same engine.</summary>
    public static JsonObject? Option(JsonObject answer, string engine, string variant) =>
        (answer["options"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(o => S(o["engine"]) == engine && S(o["variant"]) == variant);

    public static string Headline(JsonObject option)
    {
        var engine = Title(S(option["engine"]));
        var variant = S(option["variant"]);

        if (!B(option["fits"]))
        {
            return $"{engine} · {variant} · does not fit";
        }

        var where = S(option["placement"]) switch
        {
            "gpu" => $"all on the card ({Gib(option["vramMiB"])})",
            "experts-in-ram" => $"card {Gib(option["vramMiB"])} + experts in RAM {Gib(option["ramMiB"])}",
            "split" => $"split: card {Gib(option["vramMiB"])} + RAM {Gib(option["ramMiB"])}",
            "cpu" => $"CPU, RAM {Gib(option["ramMiB"])}",
            "ram" => $"in RAM {Gib(option["ramMiB"])}",
            "strata" => $"Strata, RAM {Gib(option["ramMiB"])}",
            "stream-from-disk" => "experts read from disk",
            var other => other
        };

        var ceiling = option["maxContext"] is JsonValue max && max.TryGetValue<int>(out var m)
            ? $" · up to {FormatContext(m)}" + (option["cardContext"] is JsonValue card && card.TryGetValue<int>(out var c) && c < m ? $" ({FormatContext(c)} on the card)" : "")
            : "";

        return $"{engine} · {variant} · {where} · {S(option["speed"])}{ceiling}";
    }

    public static string Model(JsonObject answer)
    {
        var model = answer["model"] as JsonObject ?? [];
        var parts = new List<string> { S(model["name"]) };

        if (model["parameters"] is JsonValue p && p.TryGetValue<long>(out var parameters))
        {
            parts.Add($"{parameters / 1e9:0.#}B parameters");
        }

        if (model["experts"] is JsonValue e && e.TryGetValue<int>(out var experts) && experts > 1)
        {
            parts.Add($"{experts} experts, {(model["expertsUsed"] is JsonValue u ? S(u) : "?")} a token");
        }

        if (model["trainedContext"] is JsonValue t && t.TryGetValue<int>(out var trained))
        {
            parts.Add($"trained for {FormatContext(trained)}");
        }

        return string.Join(" · ", parts);
    }

    public static string Machine(JsonObject answer)
    {
        var machine = answer["machine"] as JsonObject ?? [];
        var gpus = (machine["gpus"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(g => $"{S(g["name"])} {Gib(g["totalMiB"])}" + (g["freeMiB"] is JsonValue f && f.TryGetValue<long>(out var free) ? $" ({Gib(free)} free now)" : ""))
            .ToArray();

        return $"{(gpus.Length == 0 ? "no NVIDIA card seen" : string.Join(", ", gpus))} · RAM {Gib(machine["ramMiB"])} · {S(machine["cores"])} cores";
    }

    public static IReadOnlyList<string> Refused(JsonObject answer) =>
        (answer["refused"] as JsonArray ?? []).OfType<JsonObject>().Select(r => $"{Title(S(r["engine"]))}: {S(r["reason"])}").ToArray();

    public static string Title(string engine) => engine switch
    {
        "ollama" => "Ollama",
        "llamacpp" => "llama.cpp",
        "colibri" => "colibri",
        "strata" => "Strata",
        _ => engine
    };

    private static string Gib(JsonNode? mib) => mib is JsonValue v && v.TryGetValue<long>(out var m) ? Gib(m) : "?";

    private static string Gib(long mib) => mib >= 1024
        ? (mib / 1024.0).ToString(mib >= 10 * 1024 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GiB"
        : $"{mib} MiB";

    private static bool B(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string S(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString()
    };
}
