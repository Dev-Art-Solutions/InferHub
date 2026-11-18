using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

/// <summary>Something about a node an admin should look at (152 D4): <c>warn</c> needs a hand, <c>info</c> is worth knowing.</summary>
public sealed record FleetAttention(string Level, string Text)
{
    public const string Warn = "warn";
    public const string Info = "info";
}

public sealed record FleetNodeTraffic(long Completed, long Failed, double PerMinute, double? FailureRate);

public sealed record FleetNode(
    string NodeId,
    string Name,
    string Version,
    string? Organisation,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Keys,
    string State,
    double AgeSeconds,
    bool Cordoned,
    int InFlight,
    int? MaxConcurrency,
    int Models,
    string? Backend,
    bool? Sealed,
    string? Sharing,
    string? Profile,
    string? UpdateAvailable,
    long CompletedTotal,
    long FailedTotal,
    FleetNodeTraffic Recent,
    IReadOnlyList<FleetAttention> Attention);

public sealed record FleetGroup(
    string Key,
    bool Defined,
    string? Description,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<string> Absent,
    int Online,
    int Routable,
    int Cordoned,
    int InFlight,
    int? Capacity,
    long Completed,
    long Failed,
    double PerMinute,
    double? FailureRate,
    int Attention);

public sealed record FleetTotals(int Nodes, int Routable, int Cordoned, int InFlight, int? Capacity, long Completed, long Failed, double PerMinute, double? FailureRate, int Attention);

public sealed record FleetReport(
    DateTimeOffset At,
    string GroupBy,
    int Minutes,
    int SampleSeconds,
    int HistoryMinutes,
    string GroupsPersistence,
    IReadOnlyList<string> LabelKeys,
    FleetTotals Totals,
    IReadOnlyList<FleetGroup> Groups,
    IReadOnlyList<FleetNode> Nodes);

public sealed record TrafficSeries(string Key, IReadOnlyList<long> Completed, IReadOnlyList<long> Failed, IReadOnlyList<int> InFlight);

public sealed record TrafficReport(string GroupBy, int Minutes, int StepSeconds, IReadOnlyList<DateTimeOffset> Points, IReadOnlyList<bool> Covered, TrafficSeries Fleet, IReadOnlyList<TrafficSeries> Series);

/// <summary>
/// Phase 152. <c>/api/admin/fleet</c> (nodes grouped, with their recent traffic and what needs a hand), <c>/api/admin/traffic</c>
/// (the same sums over time) and <c>/api/admin/groups</c> (the groups the admin keeps, and a cordon for a whole group).
/// All of it behind the admin key like everything under <c>/api/admin</c>; the page is <c>fleet.html</c>.
/// </summary>
public static class FleetEndpoints
{
    public const string Ungrouped = "(ungrouped)";
    public const string Fleet = "fleet";

    /// <summary>A failure rate this high, over at least this many requests in the window, is worth a look (152 D4).</summary>
    public const double FailureRateWarn = 0.2;
    public const int FailureRateMinimum = 5;

    /// <summary>A node this long without a heartbeat is still in the registry, and is not answering.</summary>
    public const double SilentSeconds = 20;

    public static IEndpointRouteBuilder MapFleetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/fleet", (string? groupBy, int? minutes, IServiceProvider services) =>
        {
            var (error, by) = ParseGroupBy(groupBy);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(Report(services, DateTimeOffset.UtcNow, by, minutes));
        });

        group.MapGet("/traffic", (string? groupBy, int? minutes, IServiceProvider services) =>
        {
            var (error, by) = ParseGroupBy(groupBy);
            return error is not null
                ? Results.BadRequest(new { error })
                : Results.Ok(Traffic(services, DateTimeOffset.UtcNow, by, minutes));
        });

        group.MapGet("/groups", (NodeGroupRegistry groups) =>
            Results.Ok(new { persistence = groups.Persistence, groups = groups.All() }));

        group.MapGet("/groups/{name}", (string name, NodeGroupRegistry groups) =>
            groups.Get(name) is { } found ? Results.Ok(found) : Results.NotFound(new { error = $"group '{name}' not found" }));

        group.MapPut("/groups/{name}", (string name, NodeGroup? body, NodeGroupRegistry groups) =>
        {
            var (error, stored) = groups.Put(name, body);
            return error is not null ? Results.BadRequest(new { error }) : Results.Ok(stored);
        });

        group.MapDelete("/groups/{name}", (string name, NodeGroupRegistry groups) =>
            groups.Delete(name) ? Results.NoContent() : Results.NotFound(new { error = $"group '{name}' not found" }));

        group.MapPost("/groups/{name}/cordon", (string name, HttpContext context, IServiceProvider services) =>
            SetCordon(services, context, name, cordon: true));

        group.MapPost("/groups/{name}/uncordon", (string name, HttpContext context, IServiceProvider services) =>
            SetCordon(services, context, name, cordon: false));

        return app;
    }

    /// <summary><c>group</c> (default), <c>label:&lt;key&gt;</c>, <c>organisation</c>, <c>version</c> or <c>none</c>.</summary>
    public static (string? Error, string GroupBy) ParseGroupBy(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? "group" : value.Trim();

        if (raw.StartsWith("label:", StringComparison.OrdinalIgnoreCase))
        {
            var key = raw["label:".Length..].Trim();
            return key.Length == 0 ? ("groupBy=label: needs a label key, e.g. label:site", raw) : (null, "label:" + key);
        }

        return raw.ToLowerInvariant() switch
        {
            "group" or "organisation" or "version" or "none" => (null, raw.ToLowerInvariant()),
            _ => ($"groupBy '{raw}' is not one of group, label:<key>, organisation, version, none", raw)
        };
    }

    /// <summary>The keys a node sums into under a grouping. Several under <c>group</c>, when a node is in several groups.</summary>
    public static IReadOnlyList<string> KeysOf(
        string groupBy, string nodeId, IReadOnlyDictionary<string, string>? labels, string? organisation, string? version, NodeGroupRegistry groups)
    {
        if (groupBy.StartsWith("label:", StringComparison.Ordinal))
        {
            var key = groupBy["label:".Length..];
            return [labels is not null && labels.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : $"(no {key})"];
        }

        return groupBy switch
        {
            "organisation" => [string.IsNullOrWhiteSpace(organisation) ? "home" : organisation],
            "version" => [string.IsNullOrWhiteSpace(version) ? "unknown" : version.Split('+')[0]],
            "none" => [Fleet],
            _ => groups.GroupsOf(nodeId, labels) is { Count: > 0 } named ? named : [Ungrouped]
        };
    }

    public static FleetReport Report(IServiceProvider services, DateTimeOffset now, string groupBy = "group", int? minutes = null)
    {
        var registry = services.GetRequiredService<INodeRegistry>();
        var groups = services.GetRequiredService<NodeGroupRegistry>();
        var history = services.GetRequiredService<TrafficHistory>();
        var metrics = services.GetService<InferHub.Coordinator.Observability.Metrics>();
        var profiles = services.GetService<IProfileRegistry>();
        var updates = services.GetService<NodeUpdateRegistry>();
        var egress = services.GetService<NodeEgressRegistry>();

        var window = Math.Clamp(minutes ?? 15, 1, history.Minutes);
        var since = now.AddMinutes(-window);
        var totals = metrics?.Snapshot(now).PerNode.ToDictionary(n => n.NodeId, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, InferHub.Coordinator.Observability.NodeMetricsSnapshot>(StringComparer.OrdinalIgnoreCase);

        var nodes = registry.Snapshot(now)
            .GroupBy(n => n.NodeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(n => n.LastSeenUtc).First())
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(node =>
            {
                var (completed, failed) = history.Totals(node.NodeId, since);
                var traffic = Traffic(completed, failed, window);
                var assignment = profiles?.MatchFor(node.NodeId, node.Labels);
                var update = updates?.Of(node.NodeId);
                var attention = Attention(node, traffic, window, assignment, update, egress?.Of(node.NodeId));
                totals.TryGetValue(node.NodeId, out var total);

                return new FleetNode(
                    node.NodeId,
                    node.Name,
                    node.Version,
                    node.Organisation,
                    node.Labels,
                    groups.GroupsOf(node.NodeId, node.Labels),
                    KeysOf(groupBy, node.NodeId, node.Labels, node.Organisation, node.Version, groups),
                    StateOf(node),
                    Math.Round(node.AgeSeconds, 1),
                    node.Cordoned,
                    node.InFlight,
                    node.MaxConcurrency,
                    node.ModelCount,
                    node.BackendHealth?.ToString().ToLowerInvariant(),
                    node.Sealed,
                    node.Sharing?.Mode is { } mode ? (node.Sharing.Paused ? mode + " · paused" : mode) : null,
                    assignment?.Profile?.Name,
                    update?.Available,
                    total?.RequestsCompleted ?? 0,
                    total?.RequestsFailed ?? 0,
                    traffic,
                    attention);
            })
            .ToArray();

        var summed = new Dictionary<string, List<FleetNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            foreach (var key in node.Keys)
            {
                (summed.TryGetValue(key, out var list) ? list : summed[key] = []).Add(node);
            }
        }

        var defined = groupBy == "group" ? groups.All() : [];
        foreach (var named in defined)
        {
            summed.TryAdd(named.Name, []);
        }

        var connected = nodes.Select(n => n.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var report = summed
            .Select(pair =>
            {
                var definition = defined.FirstOrDefault(g => string.Equals(g.Name, pair.Key, StringComparison.OrdinalIgnoreCase));

                // A member named by id that is not connected: the group's own "where is it" (152 D4).
                var absent = (definition?.Nodes ?? []).Where(id => !connected.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
                return Sum(pair.Key, definition is not null, definition?.Description, pair.Value, absent, window);
            })
            .OrderBy(g => g.Key is Ungrouped ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var all = Sum(Fleet, false, null, nodes, [], window);

        return new FleetReport(
            now,
            groupBy,
            window,
            history.SampleSeconds,
            history.Minutes,
            groups.Persistence,
            nodes.SelectMany(n => n.Labels.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray(),
            new FleetTotals(nodes.Length, all.Routable, all.Cordoned, all.InFlight, all.Capacity, all.Completed, all.Failed, all.PerMinute, all.FailureRate, all.Attention + report.Where(g => g.Defined).SelectMany(g => g.Absent).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            report,
            nodes);
    }

    public static TrafficReport Traffic(IServiceProvider services, DateTimeOffset now, string groupBy = "group", int? minutes = null)
    {
        var history = services.GetRequiredService<TrafficHistory>();
        var groups = services.GetRequiredService<NodeGroupRegistry>();
        var window = Math.Clamp(minutes ?? 60, 1, history.Minutes);

        // About sixty points whatever the window, and never finer than a sample.
        var step = Math.Max(history.SampleSeconds, (int)Math.Ceiling(window * 60 / 60.0 / history.SampleSeconds) * history.SampleSeconds);
        var count = (int)Math.Ceiling(window * 60.0 / step);
        var start = now.AddSeconds(-count * step);
        var points = Enumerable.Range(1, count).Select(i => start.AddSeconds(i * step)).ToArray();

        var completed = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        var failed = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        var inFlight = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var fleetCompleted = new long[count];
        var fleetFailed = new long[count];
        var fleetInFlight = new int[count];

        // A bucket no sample fell in is not a quiet minute: the hub was not running, or not yet sampling (28 D5).
        var covered = new bool[count];

        foreach (var sample in history.Since(start))
        {
            // A sample at exactly a bucket's end belongs to that bucket: the bucket is (end - step, end].
            var index = Math.Clamp((int)Math.Ceiling((sample.At - start).TotalSeconds / step) - 1, 0, count - 1);
            covered[index] = true;
            var sampleInFlight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fleetSampleInFlight = 0;

            foreach (var (nodeId, point) in sample.Nodes)
            {
                var known = history.Known(nodeId);
                var keys = KeysOf(groupBy, nodeId, known?.Labels, known?.Organisation, known?.Version, groups);

                foreach (var key in keys)
                {
                    Get(completed, key, count)[index] += point.Completed;
                    Get(failed, key, count)[index] += point.Failed;
                    sampleInFlight[key] = sampleInFlight.GetValueOrDefault(key) + point.InFlight;
                }

                fleetCompleted[index] += point.Completed;
                fleetFailed[index] += point.Failed;
                fleetSampleInFlight += point.InFlight;
            }

            // In-flight is a gauge: a bucket shows its busiest sample, never the sum of several.
            foreach (var (key, value) in sampleInFlight)
            {
                var gaugeSeries = Get(inFlight, key, count);
                gaugeSeries[index] = Math.Max(gaugeSeries[index], value);
            }

            fleetInFlight[index] = Math.Max(fleetInFlight[index], fleetSampleInFlight);
        }

        var series = completed.Keys
            .OrderBy(k => k is Ungrouped ? 1 : 0)
            .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(k => new TrafficSeries(k, completed[k], failed[k], inFlight.TryGetValue(k, out var gauge) ? gauge : new int[count]))
            .ToArray();

        return new TrafficReport(groupBy, window, step, points, covered, new TrafficSeries(Fleet, fleetCompleted, fleetFailed, fleetInFlight), series);
    }

    private static T[] Get<T>(Dictionary<string, T[]> map, string key, int count) =>
        map.TryGetValue(key, out var found) ? found : map[key] = new T[count];

    private static FleetNodeTraffic Traffic(long completed, long failed, int minutes)
    {
        var all = completed + failed;
        return new FleetNodeTraffic(completed, failed, Math.Round(all / (double)minutes, 2), all == 0 ? null : Math.Round(failed / (double)all, 3));
    }

    private static string StateOf(NodeSnapshot node) =>
        node.AgeSeconds > SilentSeconds ? "silent"
        : node.Cordoned ? "cordoned"
        : node.BackendHealth is BackendHealth.Unreachable or BackendHealth.Wedged ? "unhealthy"
        : node.ResourceThrottled is true ? "throttled"
        : node.Sharing?.Paused is true ? "paused"
        : node.MaxConcurrency is > 0 && node.InFlight >= node.MaxConcurrency ? "full"
        : node.InFlight > 0 ? "busy"
        : "idle";

    private static IReadOnlyList<FleetAttention> Attention(
        NodeSnapshot node, FleetNodeTraffic traffic, int minutes, NodeProfileAssignment? assignment, NodeUpdateState? update, NodeEgressState? egress)
    {
        var list = new List<FleetAttention>();

        if (node.AgeSeconds > SilentSeconds)
        {
            list.Add(new(FleetAttention.Warn, $"no heartbeat for {node.AgeSeconds:F0} s"));
        }

        if (node.BackendHealth is BackendHealth.Unreachable or BackendHealth.Wedged)
        {
            list.Add(new(FleetAttention.Warn, $"its backend is {node.BackendHealth.ToString()!.ToLowerInvariant()}: it takes no new work"));
        }

        if (node.ResourceThrottled is true)
        {
            list.Add(new(FleetAttention.Warn, "throttled by its own resource limit"));
        }

        if (traffic.FailureRate is >= FailureRateWarn && traffic.Completed + traffic.Failed >= FailureRateMinimum)
        {
            list.Add(new(FleetAttention.Warn, $"{traffic.FailureRate:P0} of {traffic.Completed + traffic.Failed} requests failed in the last {minutes} min"));
        }

        if (assignment?.Conflicts is { Count: > 0 } conflicts)
        {
            list.Add(new(FleetAttention.Warn, $"profiles conflict ({string.Join(", ", conflicts)}): none applies"));
        }

        if (node.Sealed is true && egress is { Open.Count: > 0 })
        {
            list.Add(new(FleetAttention.Warn, $"sealed, and its machine reaches {string.Join(", ", egress.Open)}"));
        }

        if (node.ModelCount == 0 && node.BackendHealth is not (BackendHealth.Unreachable or BackendHealth.Wedged))
        {
            list.Add(new(FleetAttention.Info, "holds no model"));
        }

        if (node.Cordoned)
        {
            list.Add(new(FleetAttention.Info, "cordoned: takes no new work"));
        }

        if (node.Sharing?.Paused is true)
        {
            list.Add(new(FleetAttention.Info, $"paused by its owner{(string.IsNullOrWhiteSpace(node.Sharing.Reason) ? "" : ": " + node.Sharing.Reason)}"));
        }

        if (node.MaxConcurrency is > 0 && node.InFlight >= node.MaxConcurrency)
        {
            list.Add(new(FleetAttention.Info, $"at its cap: {node.InFlight} of {node.MaxConcurrency}"));
        }

        if (!string.IsNullOrWhiteSpace(update?.Available))
        {
            list.Add(new(FleetAttention.Info, $"update available: {update.Available}"));
        }

        return list;
    }

    private static FleetGroup Sum(string key, bool defined, string? description, IReadOnlyCollection<FleetNode> nodes, IReadOnlyList<string> absent, int minutes)
    {
        var completed = nodes.Sum(n => n.Recent.Completed);
        var failed = nodes.Sum(n => n.Recent.Failed);
        var all = completed + failed;

        // A capacity is only a number when every member declared one; one unbounded node makes the sum unbounded (126 D3).
        int? capacity = nodes.Count > 0 && nodes.All(n => n.MaxConcurrency is > 0) ? nodes.Sum(n => n.MaxConcurrency!.Value) : null;

        return new FleetGroup(
            key,
            defined,
            description,
            nodes.Select(n => n.NodeId).ToArray(),
            absent,
            nodes.Count(n => n.State != "silent"),
            nodes.Count(n => n.State is "idle" or "busy" or "full"),
            nodes.Count(n => n.Cordoned),
            nodes.Sum(n => n.InFlight),
            capacity,
            completed,
            failed,
            Math.Round(all / (double)minutes, 2),
            all == 0 ? null : Math.Round(failed / (double)all, 3),
            nodes.Count(n => n.Attention.Any(a => a.Level == FleetAttention.Warn)) + absent.Count);
    }

    private static IResult SetCordon(IServiceProvider services, HttpContext context, string name, bool cordon)
    {
        var groups = services.GetRequiredService<NodeGroupRegistry>();
        var group = groups.Get(name);

        if (group is null)
        {
            return Results.NotFound(new { error = $"group '{name}' not found" });
        }

        var registry = services.GetRequiredService<INodeRegistry>();
        var audit = services.GetRequiredService<IAuditLog>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("InferHub.Coordinator.Endpoints.Fleet");
        var ip = context.Connection.RemoteIpAddress;
        var by = ip is null ? "admin" : System.Net.IPAddress.IsLoopback(ip) ? "local" : ip.ToString();
        var action = cordon ? "cordon" : "uncordon";
        var now = DateTimeOffset.UtcNow;

        var members = registry.Snapshot(now)
            .Where(n => group.Contains(n.NodeId, n.Labels))
            .Select(n => n.NodeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var changed = new List<string>();
        foreach (var nodeId in members)
        {
            // The per-node routes' own calls, one node at a time, each in the per-node audit (152 D5).
            if (cordon ? registry.Cordon(nodeId) : registry.Uncordon(nodeId))
            {
                audit.Record(nodeId, action, $"{by} (group {group.Name})", now);
                changed.Add(nodeId);
            }
        }

        var connected = members.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var absent = (group.Nodes ?? []).Where(id => !connected.Contains(id)).ToArray();
        logger.LogInformation("{Action} group {Group}: {Count} node(s)", action, group.Name, changed.Count);
        return Results.Ok(new { group = group.Name, cordoned = cordon, nodes = changed, absent });
    }
}
