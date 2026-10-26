using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Node.Fit;

/// <summary>
/// The context the owner chose for an Ollama model when installing it (phase 118, D4), kept in
/// <c>&lt;DataDirectory&gt;/model-contexts.json</c> beside the control token and added as <c>options.num_ctx</c> to a
/// request for that model that sets none — the caller's own value always wins.
/// </summary>
/// <remarks>
/// A file rather than <c>node.local.json</c>: an Ollama name (<c>hf.co/owner/repo:Q4_K_M</c>) carries the
/// <c>:</c> configuration keys are split on. Losing the file costs Ollama's default context, never a wrong answer.
/// </remarks>
public sealed class ModelContexts(string path)
{
    public const string FileName = "model-contexts.json";

    private readonly object gate = new();
    private Dictionary<string, int>? cache;

    public string FilePath => path;

    public int? For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        lock (gate)
        {
            return Load().TryGetValue(Key(model), out var context) ? context : null;
        }
    }

    public void Set(string model, int? context)
    {
        lock (gate)
        {
            var all = new Dictionary<string, int>(Load(), StringComparer.Ordinal);

            if (context is > 0)
            {
                all[Key(model)] = context.Value;
            }
            else
            {
                all.Remove(Key(model));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)));
            File.Move(temporary, path, overwrite: true);
            cache = all;
        }
    }

    /// <summary>The request with <c>options.num_ctx</c> added when the owner chose one for its model and the request set none.</summary>
    public string Apply(string requestJson)
    {
        try
        {
            if (JsonNode.Parse(requestJson) is not JsonObject request
                || For(request["model"]?.GetValue<string>()) is not { } context)
            {
                return requestJson;
            }

            if (request["options"] is not JsonObject options)
            {
                options = new JsonObject();
                request["options"] = options;
            }

            if (options.ContainsKey("num_ctx"))
            {
                return requestJson;
            }

            options["num_ctx"] = context;
            return request.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return requestJson;
        }
    }

    /// <summary>Ollama's own equivalence: case does not matter and <c>:latest</c> is the bare name.</summary>
    internal static string Key(string model)
    {
        var key = model.Trim().ToLowerInvariant();
        return key.EndsWith(":latest", StringComparison.Ordinal) ? key[..^7] : key;
    }

    private Dictionary<string, int> Load()
    {
        if (cache is not null)
        {
            return cache;
        }

        try
        {
            cache = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            cache = new();
        }

        return cache;
    }
}
