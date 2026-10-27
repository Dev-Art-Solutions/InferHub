using System.Text.Json;

namespace InferHub.Shared.Fit;

/// <summary>
/// Hugging Face's model listing for the catalogue (119 D3): GGUF repositories of a kind, in the Hub's own order, with the
/// facts the list needs — the parameter count, the architecture, the trained context — in the one call.
/// </summary>
public static class HuggingFaceListing
{
    public sealed record Model(
        string Id,
        string? Task,
        long? Parameters,
        string? Architecture,
        int? TrainedContext,
        bool Moe,
        long? Downloads,
        long? Likes,
        DateTimeOffset? Created,
        DateTimeOffset? Modified);

    /// <summary>The relative API path for a sort, a kind and a search.</summary>
    public static string Query(string sort, string kind, string? search, int limit)
    {
        var order = sort switch
        {
            CatalogSorts.Downloads => "downloads",
            CatalogSorts.Newest => "createdAt",
            _ => "trendingScore"
        };
        var task = kind == CatalogKinds.Vision ? "image-text-to-text" : "text-generation";
        var query = $"api/models?filter=gguf&pipeline_tag={task}&sort={order}&direction=-1&limit={Math.Clamp(limit, 1, 100)}";

        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&search=" + Uri.EscapeDataString(search.Trim());
        }

        foreach (var field in new[] { "gguf", "downloads", "likes", "pipeline_tag", "tags", "createdAt", "lastModified" })
        {
            query += $"&expand[]={field}";
        }

        return query;
    }

    public static IReadOnlyList<Model> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Hugging Face's listing is not an array of models");
        }

        var models = new List<Model>();

        foreach (var item in root.EnumerateArray())
        {
            if (Text(item, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            long? parameters = null;
            string? architecture = null;
            int? context = null;

            if (item.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
            {
                parameters = gguf.TryGetProperty("total", out var total) && total.TryGetInt64(out var n) && n > 0 ? n : null;
                architecture = Text(gguf, "architecture");
                context = gguf.TryGetProperty("context_length", out var ctx) && ctx.TryGetInt32(out var c) && c > 0 ? c : null;
            }

            var tags = item.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToArray()
                : [];
            var moe = tags.Contains("moe", StringComparer.OrdinalIgnoreCase)
                      || architecture?.Contains("moe", StringComparison.OrdinalIgnoreCase) == true;

            models.Add(new Model(
                id,
                Text(item, "pipeline_tag"),
                parameters,
                architecture,
                context,
                moe,
                Number(item, "downloads"),
                Number(item, "likes"),
                Date(item, "createdAt"),
                Date(item, "lastModified")));
        }

        return models;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
