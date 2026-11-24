using System.Text.Json;
using InferHub.Shared.Fit;

namespace InferHub.Coordinator.Services;

/// <summary>Phase 148: one node's part in one group since the coordinator started.</summary>
/// <param name="Seconds">How long a running group listed it among its workers.</param>
/// <param name="MiBHours">Its offer (134 D4) over that time.</param>
/// <param name="Requests">Responses the group's head returned while it was a worker.</param>
/// <param name="ShareTokens">Those tokens × the layers it held ÷ the group's layers (D2).</param>
public sealed record ContributionGroup(
    string Group,
    string Model,
    bool Community,
    double Seconds,
    double MiBHours,
    long Requests,
    long PromptTokens,
    long GeneratedTokens,
    double ShareTokens);

/// <summary>Phase 148: one node — what it gives now and what it has given, summed over its groups.</summary>
public sealed record ContributionNode(
    string NodeId,
    string Name,
    string? Organisation,
    bool Connected,
    long? OfferMiB,
    IReadOnlyList<string> GroupsNow,
    double Seconds,
    double MiBHours,
    long Requests,
    long PromptTokens,
    long GeneratedTokens,
    double ShareTokens,
    IReadOnlyList<ContributionGroup> Groups);

public sealed record ContributionReport(DateTimeOffset Since, int SampleSeconds, IReadOnlyList<ContributionNode> Nodes);

/// <summary>
/// Phase 148: which node gave how much memory and how many tokens to groups — shown, never enforced. Everything comes from what
/// the heads already report (136, 137) and the counts every response already carries (26); in memory only (rule 4), so a
/// restart starts it again and <see cref="ContributionReport.Since"/> says from when.
/// </summary>
public sealed class ShardContribution : BackgroundService
{
    /// <summary>D3: how often the heads' reports are read for memory and time.</summary>
    public const int SampleSeconds = 10;

    /// <summary>D3: a sampler that did not run for longer credits at most this many samples, never the whole gap.</summary>
    private const int MaxSamplesPerGap = 3;

    private readonly NodeShardRegistry shards;
    private readonly INodeRegistry registry;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<(string NodeId, string Group), Tally> tallies = new();
    private readonly Dictionary<string, (string Name, string? Organisation)> known = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? last;

    public ShardContribution(NodeShardRegistry shards, INodeRegistry registry, TimeProvider time)
    {
        this.shards = shards;
        this.registry = registry;
        this.time = time;
        Since = time.GetUtcNow();
    }

    public DateTimeOffset Since { get; }

    private sealed class Tally
    {
        public string Model = "";
        public bool Community;
        public double Seconds;
        public double MiBSeconds;
        public long Requests;
        public long Prompt;
        public long Generated;
        public double Share;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SampleSeconds), time);
        Sample(time.GetUtcNow());

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Sample(time.GetUtcNow());
        }
    }

    /// <summary>D3: each worker of each group a head reports running is credited its offer for the time since the last sample.</summary>
    public void Sample(DateTimeOffset now)
    {
        var nodes = registry.Snapshot(now);

        lock (gate)
        {
            var seconds = last is { } before ? Math.Clamp((now - before).TotalSeconds, 0, SampleSeconds * MaxSamplesPerGap) : 0;
            last = now;
            Remember(nodes);

            if (seconds <= 0)
            {
                return;
            }

            foreach (var (group, workers) in Running())
            {
                foreach (var worker in workers)
                {
                    var nodeId = Resolve(worker.Name, nodes);
                    var tally = TallyOf(nodeId, group);
                    tally.Seconds += seconds;
                    tally.MiBSeconds += OfferOf(nodeId) * seconds;
                }
            }
        }
    }

    /// <summary>
    /// D1: a response the dispatcher received from <paramref name="nodeId"/> for <paramref name="model"/>. Credited only when that
    /// node reports a running group of that name — every worker it lists gets the tokens, and its share by layers (D2).
    /// </summary>
    public void RecordTokens(string nodeId, string? model, string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(model) || string.IsNullOrEmpty(responseJson)
            || shards.Of(nodeId)?.Groups?.FirstOrDefault(g => g.State == ShardGroupState.Running
                && string.Equals(g.Name, model!.Trim(), StringComparison.OrdinalIgnoreCase)
                && g.Layers is { Count: > 0 }) is not { } group
            || !TryCounts(responseJson, out var prompt, out var generated))
        {
            return;
        }

        var nodes = registry.Snapshot(time.GetUtcNow());
        var layers = group.Layers!;
        var total = layers.Sum(l => Math.Max(0, l.Count));

        lock (gate)
        {
            Remember(nodes);

            foreach (var worker in layers)
            {
                var tally = TallyOf(Resolve(worker.Name, nodes), group);
                tally.Requests++;
                tally.Prompt += prompt;
                tally.Generated += generated;
                tally.Share += total > 0 ? (prompt + generated) * (double)Math.Max(0, worker.Count) / total : (prompt + generated) / (double)layers.Count;
            }
        }
    }

    /// <summary>For <c>GET /api/admin/shards/contribution</c>: every node that offers or has given, most given first.</summary>
    public ContributionReport Report()
    {
        var now = time.GetUtcNow();
        var nodes = registry.Snapshot(now);
        var connected = nodes.Select(n => n.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (gate)
        {
            Remember(nodes);
            var groupsNow = Running()
                .SelectMany(r => r.Workers.Select(w => (NodeId: Resolve(w.Name, nodes), Group: r.Group.Name)))
                .GroupBy(x => x.NodeId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Group).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);

            var ids = tallies.Keys.Select(k => k.NodeId)
                .Concat(shards.All().Where(s => s.State != NodeShardState.Off).Select(s => s.NodeId))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            var rows = ids.Select(id =>
            {
                var groups = tallies.Where(t => string.Equals(t.Key.NodeId, id, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new ContributionGroup(t.Key.Group, t.Value.Model, t.Value.Community, Math.Round(t.Value.Seconds, 1),
                        Math.Round(t.Value.MiBSeconds / 3600, 3), t.Value.Requests, t.Value.Prompt, t.Value.Generated, Math.Round(t.Value.Share, 1)))
                    .OrderByDescending(g => g.ShareTokens).ThenByDescending(g => g.MiBHours).ThenBy(g => g.Group, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var who = known.TryGetValue(id, out var k) ? k : (Name: id, Organisation: null);
                var offer = connected.Contains(id) && shards.Of(id) is { State: not NodeShardState.Off } state ? state.RamMiB + state.VramMiB : (long?)null;

                return new ContributionNode(id, who.Name, who.Organisation, connected.Contains(id), offer,
                    groupsNow.TryGetValue(id, out var inGroups) ? inGroups : [],
                    groups.Sum(g => g.Seconds), Math.Round(groups.Sum(g => g.MiBHours), 3), groups.Sum(g => g.Requests),
                    groups.Sum(g => g.PromptTokens), groups.Sum(g => g.GeneratedTokens), Math.Round(groups.Sum(g => g.ShareTokens), 1), groups);
            })
            .OrderByDescending(n => n.ShareTokens).ThenByDescending(n => n.MiBHours).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

            return new ContributionReport(Since, SampleSeconds, rows);
        }
    }

    /// <summary>Every group a head reports running, with the workers it lists — the head's own worker first (137 D4).</summary>
    private IEnumerable<(ShardGroupState Group, IReadOnlyList<ShardGroupLayers> Workers)> Running() =>
        shards.All()
            .SelectMany(s => s.Groups ?? [])
            .Where(g => g.State == ShardGroupState.Running && g.Layers is { Count: > 0 })
            .Select(g => (g, g.Layers!));

    private Tally TallyOf(string nodeId, ShardGroupState group)
    {
        if (!tallies.TryGetValue((nodeId, group.Name), out var tally))
        {
            tallies[(nodeId, group.Name)] = tally = new Tally();
        }

        tally.Model = group.Model;
        tally.Community = group.Community;
        return tally;
    }

    private long OfferOf(string nodeId) =>
        shards.Of(nodeId) is { } state ? Math.Max(0, state.RamMiB + state.VramMiB) : 0;

    /// <summary>A head names its workers by node name (137 D4); a connected node's id, else one seen before, else the name itself.</summary>
    private string Resolve(string name, IReadOnlyCollection<NodeSnapshot> nodes)
    {
        if (nodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)) is { } node)
        {
            return node.NodeId;
        }

        return known.FirstOrDefault(k => string.Equals(k.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Key ?? name;
    }

    private void Remember(IReadOnlyCollection<NodeSnapshot> nodes)
    {
        foreach (var node in nodes)
        {
            known[node.NodeId] = (node.Name, node.Organisation);
        }
    }

    /// <summary>The two counts of 26; a response with neither credits nothing.</summary>
    private static bool TryCounts(string json, out long prompt, out long generated)
    {
        prompt = 0;
        generated = 0;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("prompt_eval_count", out var p) && p.ValueKind == JsonValueKind.Number)
            {
                prompt = Math.Max(0, p.GetInt64());
            }

            if (root.TryGetProperty("eval_count", out var e) && e.ValueKind == JsonValueKind.Number)
            {
                generated = Math.Max(0, e.GetInt64());
            }

            return prompt + generated > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
