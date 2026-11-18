using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 152: a named set of nodes the admin keeps on the hub — by node id, by an exact match on every label pair given,
/// or both (a node is a member when either holds). A group changes nothing about routing (152 D1): it is how the fleet
/// page sums traffic and what a bulk cordon acts on.
/// </summary>
public sealed record NodeGroup(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("nodes")] IReadOnlyList<string>? Nodes = null,
    [property: JsonPropertyName("labels")] IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>A group that names nothing has no members — never every node (the profile selector's rule, 43 D2).</summary>
    [JsonIgnore]
    public bool IsEmpty => (Nodes is null || Nodes.Count == 0) && (Labels is null || Labels.Count == 0);

    public bool Contains(string nodeId, IReadOnlyDictionary<string, string>? labels)
    {
        if (Nodes is not null && Nodes.Any(id => string.Equals(id.Trim(), nodeId?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Labels is null || Labels.Count == 0 || labels is null)
        {
            return false;
        }

        foreach (var pair in Labels)
        {
            if (!labels.TryGetValue(pair.Key, out var value)
                || !string.Equals(value?.Trim(), pair.Value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary><c>Fleet:Groups</c> (phase 152). The tenth recorded exception to rule 4 when <c>Persistence=file</c>.</summary>
public sealed class NodeGroupOptions
{
    public const string PersistenceNone = "none";
    public const string PersistenceFile = "file";

    /// <summary><c>none</c> (default: groups live as long as this coordinator) or <c>file</c> (one <c>groups.json</c>).</summary>
    public string Persistence { get; set; } = PersistenceNone;

    /// <summary>Relative by default for a bare-metal hub; the image sets <c>/data/groups</c> (the permissions trap, 21 D7).</summary>
    public string DataDirectory { get; set; } = "./data/groups";

    public bool IsFile => string.Equals((Persistence ?? "").Trim(), PersistenceFile, StringComparison.OrdinalIgnoreCase);

    public bool IsValid =>
        string.IsNullOrWhiteSpace(Persistence)
        || string.Equals(Persistence.Trim(), PersistenceNone, StringComparison.OrdinalIgnoreCase)
        || IsFile;
}

/// <summary>
/// The hub's book of node groups (phase 152). Membership is computed on read from what each node reports, so a node that
/// comes back under a new connection, or changes its labels, is in the right groups on the next read with nothing to heal.
/// </summary>
public sealed class NodeGroupRegistry
{
    public const int MaxGroups = 200;
    public const int MaxNodesPerGroup = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, NodeGroup> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly object writeLock = new();
    private readonly string? file;
    private readonly ILogger<NodeGroupRegistry> logger;

    public NodeGroupRegistry(IOptions<FleetOptions> options, ILogger<NodeGroupRegistry> logger)
    {
        this.logger = logger;
        var value = options.Value.Groups;
        Persistence = value.IsFile ? NodeGroupOptions.PersistenceFile : NodeGroupOptions.PersistenceNone;

        if (!value.IsFile)
        {
            return;
        }

        var directory = string.IsNullOrWhiteSpace(value.DataDirectory) ? "./data/groups" : value.DataDirectory;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "groups.json");

        if (File.Exists(file))
        {
            // A file someone broke by hand stops the hub rather than starting with no groups and then overwriting the
            // file with that emptiness on the first save (the same reasoning as Fleet:Profiles' typo check).
            var stored = JsonSerializer.Deserialize<NodeGroup[]>(File.ReadAllText(file), JsonOptions) ?? [];
            foreach (var group in stored.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
            {
                groups[group.Name.Trim()] = group;
            }

            logger.LogInformation("Loaded {Count} node group(s) from {File}", groups.Count, file);
        }
    }

    public string Persistence { get; }

    public IReadOnlyList<NodeGroup> All() =>
        groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public NodeGroup? Get(string name) => string.IsNullOrWhiteSpace(name) ? null : groups.GetValueOrDefault(name.Trim());

    /// <summary>The names of the groups a node belongs to, in name order.</summary>
    public IReadOnlyList<string> GroupsOf(string nodeId, IReadOnlyDictionary<string, string>? labels) =>
        groups.Values
            .Where(g => g.Contains(nodeId, labels))
            .Select(g => g.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>Validates and stores a group. Returns the refusal, or null and the stored group.</summary>
    public (string? Error, NodeGroup? Stored) Put(string name, NodeGroup? body)
    {
        var trimmed = (name ?? "").Trim();

        if (trimmed.Length is 0 or > 64 || trimmed.Any(c => char.IsControl(c) || c is '/' or '\\' or '"' or '\''))
        {
            return ("a group name is 1 to 64 characters, without slashes, quotes or control characters", null);
        }

        var nodes = (body?.Nodes ?? [])
            .Select(id => id?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (nodes.Length > MaxNodesPerGroup)
        {
            return ($"a group names at most {MaxNodesPerGroup} nodes; use labels for a larger set", null);
        }

        var labels = (body?.Labels ?? new Dictionary<string, string>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key.Trim(), pair => (pair.Value ?? "").Trim(), StringComparer.Ordinal);

        var stored = new NodeGroup(
            trimmed,
            string.IsNullOrWhiteSpace(body?.Description) ? null : body!.Description!.Trim(),
            nodes.Length == 0 ? null : nodes,
            labels.Count == 0 ? null : labels);

        if (stored.IsEmpty)
        {
            return ("a group names at least one node id or one label pair; an empty group would match nothing", null);
        }

        lock (writeLock)
        {
            if (!groups.ContainsKey(trimmed) && groups.Count >= MaxGroups)
            {
                return ($"this coordinator keeps at most {MaxGroups} groups", null);
            }

            groups[trimmed] = stored;
            Save();
        }

        logger.LogInformation("Stored node group '{Group}' ({Nodes} node id(s), {Labels} label pair(s))", trimmed, nodes.Length, labels.Count);
        return (null, stored);
    }

    public bool Delete(string name)
    {
        lock (writeLock)
        {
            if (string.IsNullOrWhiteSpace(name) || !groups.TryRemove(name.Trim(), out _))
            {
                return false;
            }

            Save();
        }

        logger.LogInformation("Deleted node group '{Group}'", name.Trim());
        return true;
    }

    private void Save()
    {
        if (file is null)
        {
            return;
        }

        // Write whole and move over: a crash mid-write leaves the previous file, never half of one.
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(All(), JsonOptions));
        File.Move(temp, file, overwrite: true);
    }
}
