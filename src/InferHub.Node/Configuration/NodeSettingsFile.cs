using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InferHub.Node.Configuration;

/// <summary>
/// The operator's settings for an installed node, outside its program directory (phase 101, D1):
/// <c>%ProgramData%\InferHub\Node\node.settings.json</c>, layered after <c>appsettings*.json</c> and before
/// environment variables. An update replaces every file in <c>Program Files</c> and loses none of it.
/// </summary>
/// <remarks>
/// Written by the setup through <c>InferHub.Node.Service.exe configure --input &lt;file&gt;</c>: one
/// <c>Section:Key=value</c> per line, applied in order. <c>Key=</c> with nothing after it removes the key (a
/// whole section, for <c>Node:Labels=</c>), so a re-run can replace a set rather than only add to it.
/// <c>true</c>/<c>false</c> and integers are written as JSON literals; everything else as a string.
/// </remarks>
public static class NodeSettingsFile
{
    public const string FileName = "node.settings.json";

    /// <summary><c>%ProgramData%\InferHub\Node</c> — where the Windows setup puts it, and where the service host looks.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InferHub", "Node");

    public static string DefaultPath => Path.Combine(DefaultDirectory, FileName);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Parses the <c>configure</c> input: <c>Key=value</c> lines, <c>#</c> comments and blank lines skipped.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseInput(string text)
    {
        var values = new List<KeyValuePair<string, string>>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var equals = line.IndexOf('=');

            if (equals <= 0)
            {
                throw new FormatException($"'{line}' is not Key=value");
            }

            values.Add(new(line[..equals].Trim(), line[(equals + 1)..].Trim()));
        }

        return values;
    }

    /// <summary>Merges <paramref name="values"/> into the file at <paramref name="path"/>, creating it if needed.</summary>
    public static void Apply(string path, IEnumerable<KeyValuePair<string, string>> values)
    {
        var root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
            ? existing
            : new JsonObject();

        foreach (var (key, value) in values)
        {
            Set(root, key, value);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written in place rather than replaced, so an ACL the setup put on the file stays on it.
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        root.WriteTo(writer, Indented);
    }

    internal static void Set(JsonObject root, string key, string value)
    {
        var segments = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            throw new FormatException($"'{key}' is not a configuration key");
        }

        var parent = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var name = Find(parent, segments[i]) ?? segments[i];

            if (parent[name] is not JsonObject child)
            {
                if (value.Length == 0)
                {
                    return;
                }

                child = new JsonObject();
                parent[name] = child;
            }

            parent = child;
        }

        var leaf = Find(parent, segments[^1]) ?? segments[^1];

        if (value.Length == 0)
        {
            parent.Remove(leaf);
            return;
        }

        parent[leaf] = Literal(value);
    }

    /// <summary>Configuration keys are case-insensitive; the file keeps whatever case it already had.</summary>
    private static string? Find(JsonObject parent, string name)
        => parent.Select(pair => pair.Key).FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

    private static JsonNode Literal(string value)
    {
        if (bool.TryParse(value, out var flag))
        {
            return JsonValue.Create(flag);
        }

        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            && number.ToString(CultureInfo.InvariantCulture) == value)
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }
}
