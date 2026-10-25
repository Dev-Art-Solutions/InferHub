using System.Text.Json.Nodes;

namespace InferHub.Desktop;

/// <summary>
/// Phase 117 in the app: the Models tab's "Shared" box, one model at a time, turned into the node's
/// <c>Node:Sharing:Models</c> lists. An owner who wrote an <c>Include</c> list keeps it — the box adds to it or takes
/// from it — and everyone else gets an <c>Exclude</c> list: a model pulled later is shared, as it was before.
/// </summary>
public static class SharedModels
{
    /// <summary>The lists after <paramref name="model"/> is shared or kept; <c>Error</c> when that cannot be said in them.</summary>
    public static (string[] Include, string[] Exclude, string? Error) Toggle(JsonNode? sharing, string model, bool shared)
    {
        var include = Names(sharing?["include"]);
        var exclude = Names(sharing?["exclude"]);
        var name = model.Trim();

        exclude.RemoveAll(n => Same(n, name));

        if (include.Count > 0)
        {
            include.RemoveAll(n => Same(n, name));

            if (shared)
            {
                include.Add(name);
            }
            else if (include.Count == 0)
            {
                // An empty Include is "every model": taking the last one away would share them all.
                return ([name], [.. exclude], $"{name} is the only model this node shares; to share none, pause sharing on the Overview tab.");
            }
        }
        else if (!shared)
        {
            exclude.Add(name);
        }

        return ([.. include], [.. exclude], null);
    }

    /// <summary>"All shared", or how many are kept — the Overview line.</summary>
    public static string Describe(JsonNode? status)
    {
        // A 3.77 node says nothing per model, and then the app says nothing either. The same for a model Node:Models
        // leaves out (no field, or null): served to nobody, so counted on neither side.
        var models = (status?["models"] as JsonArray ?? []).Where(m => m?["shared"] is JsonValue).ToArray();

        if (models.Length == 0)
        {
            return "";
        }

        var kept = models.Where(m => !m!["shared"]!.GetValue<bool>()).Select(m => m?["name"]?.GetValue<string>()).ToArray();

        return kept.Length == 0
            ? $"The network may use all {models.Length} models."
            : $"Kept for this machine: {string.Join(", ", kept)} — the network may use {models.Length - kept.Length} of {models.Length} models.";
    }

    private static List<string> Names(JsonNode? list) =>
        list is JsonArray array
            ? array.Select(n => n?.GetValue<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToList()
            : [];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
