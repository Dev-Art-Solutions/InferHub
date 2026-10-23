using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Node.Configuration;

/// <summary>
/// <c>node.local.json</c> — the "changed on this box" layer, read last and reloaded on change (82 D7) — and, since
/// phase 112, the one place the control API writes. Its path is resolved once, before the host is built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside the binaries by default</b>, as 82 D7 pinned it (<see cref="AppContext.BaseDirectory"/>, so the admin
/// pages and the configuration watch one file). <c>Node:LocalSettingsFile</c> (or <c>INFERHUB_NODE_LOCAL_SETTINGS</c>)
/// moves it — the desktop app's own node (115) keeps it in the user's app-data directory, because the directory an
/// app is installed into is not one its user may write.
/// </para>
/// <para>
/// <b>Writes are atomic</b>: a temp file beside it, then a rename, the previous file kept as <c>.bak</c>. A torn
/// write would be a node that does not start, and the reload-on-change watcher reads whatever is there.
/// </para>
/// </remarks>
/// <summary>This host's <c>node.local.json</c> (phase 112), resolved once per host — two hosts in one process (the tests) keep two files.</summary>
public sealed record LocalSettingsFile(string Path);

public static class NodeLocalSettings
{
    public const string FileName = "node.local.json";

    public const string PathKey = "Node:LocalSettingsFile";

    public const string PathVariable = "INFERHUB_NODE_LOCAL_SETTINGS";

    /// <summary>Resolves the path from configuration; the composition root calls it once and registers <see cref="LocalSettingsFile"/>.</summary>
    public static string Resolve(IConfiguration configuration)
    {
        var configured = Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } fromEnv
            ? fromEnv
            : configuration[PathKey];

        return string.IsNullOrWhiteSpace(configured)
            ? System.IO.Path.Combine(AppContext.BaseDirectory, FileName)
            : System.IO.Path.GetFullPath(configured.Trim(), AppContext.BaseDirectory);
    }

    /// <summary>The file's content as an object; empty when there is none.</summary>
    public static JsonObject Read(string path)
    {

        if (!File.Exists(path))
        {
            return [];
        }

        var text = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
    }

    /// <summary>Writes <paramref name="content"/> atomically, keeping the previous file as <c>.bak</c>.</summary>
    public static void Write(JsonObject content, string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Sets <paramref name="key"/> (<c>Section:Sub:Key</c>, the configuration's own form) in <paramref name="root"/>,
    /// creating the objects on the way; a null value writes JSON null, which masks a lower layer's value.
    /// </summary>
    public static void Set(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = root;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var name = Existing(current, parts[i]) ?? parts[i];

            if (current[name] is not JsonObject next)
            {
                next = [];
                current[name] = next;
            }

            current = next;
        }

        var last = Existing(current, parts[^1]) ?? parts[^1];
        current[last] = value?.DeepClone();
    }

    /// <summary>Removes <paramref name="key"/> from <paramref name="root"/>, and any object it leaves empty.</summary>
    public static bool Remove(JsonObject root, string key)
    {
        var parts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var trail = new List<(JsonObject Parent, string Name)>();
        var current = root;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (Existing(current, parts[i]) is not { } name || current[name] is not JsonObject next)
            {
                return false;
            }

            trail.Add((current, name));
            current = next;
        }

        if (Existing(current, parts[^1]) is not { } last)
        {
            return false;
        }

        current.Remove(last);

        for (var i = trail.Count - 1; i >= 0 && current.Count == 0; i--)
        {
            trail[i].Parent.Remove(trail[i].Name);
            current = trail[i].Parent;
        }

        return true;
    }

    /// <summary>The configuration is case-insensitive; the file keeps whatever spelling it already has.</summary>
    private static string? Existing(JsonObject node, string name) =>
        node.Select(pair => pair.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
}
