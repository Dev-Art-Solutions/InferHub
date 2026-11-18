using System.Collections.Concurrent;
using InferHub.Coordinator.Observability;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary><c>Fleet:Traffic</c> (phase 152): how often the hub samples its own counters, and how long it keeps them.</summary>
public sealed class TrafficHistoryOptions
{
    public int SampleSeconds { get; set; } = 10;

    public int Minutes { get; set; } = 60;
}

/// <summary>One node in one sample: requests finished and failed since the previous sample, and the hub's jobs on it now.</summary>
public readonly record struct TrafficPoint(long Completed, long Failed, int InFlight, int? Capacity);

public sealed record TrafficSample(DateTimeOffset At, IReadOnlyDictionary<string, TrafficPoint> Nodes);

/// <summary>What the history last knew about a node, so a node that has left still sums into its group (152 D3).</summary>
public sealed record TrafficNode(string NodeId, string Name, IReadOnlyDictionary<string, string> Labels, string? Organisation, string Version);

/// <summary>
/// Phase 152: the last hour of traffic, per node, in memory. It reads <see cref="Metrics"/>' per-node counters and the
/// registry's in-flight counts — the same numbers <c>/metrics</c> exposes — and keeps the differences, so the console can
/// draw a rate without a Prometheus. It measures nothing new (28 D2), and a restart loses it like every other counter (rule 4).
/// </summary>
public sealed class TrafficHistory : BackgroundService
{
    private readonly Metrics metrics;
    private readonly INodeRegistry registry;
    private readonly TimeProvider time;
    private readonly ConcurrentQueue<TrafficSample> samples = new();
    private readonly ConcurrentDictionary<string, TrafficNode> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sampleLock = new();
    private Dictionary<string, (long Completed, long Failed)>? previous;

    public TrafficHistory(Metrics metrics, INodeRegistry registry, IOptions<FleetOptions> options, TimeProvider time)
    {
        this.metrics = metrics;
        this.registry = registry;
        this.time = time;
        SampleSeconds = Math.Clamp(options.Value.Traffic.SampleSeconds, 1, 300);
        Minutes = Math.Clamp(options.Value.Traffic.Minutes, 1, 1440);
    }

    public int SampleSeconds { get; }

    public int Minutes { get; }

    private int Capacity => Math.Max(1, Minutes * 60 / SampleSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(SampleSeconds), time);
        Sample(time.GetUtcNow());

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Sample(time.GetUtcNow());
        }
    }

    /// <summary>
    /// Takes one sample. The first call only sets the baseline: a difference against nothing would put every request since
    /// the hub started into the first ten seconds, a spike that never happened.
    /// </summary>
    public void Sample(DateTimeOffset now)
    {
        lock (sampleLock)
        {
            var counters = metrics.Snapshot(now).PerNode.ToDictionary(
                n => n.NodeId, n => (n.RequestsCompleted, n.RequestsFailed), StringComparer.OrdinalIgnoreCase);
            var nodes = registry.Snapshot(now);

            foreach (var node in nodes)
            {
                known[node.NodeId] = new TrafficNode(node.NodeId, node.Name, node.Labels, node.Organisation, node.Version);
            }

            if (previous is null)
            {
                previous = counters;
                return;
            }

            var points = new Dictionary<string, TrafficPoint>(StringComparer.OrdinalIgnoreCase);
            var live = nodes.GroupBy(n => n.NodeId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var id in counters.Keys.Union(live.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var (completed, failed) = counters.GetValueOrDefault(id);
                var (lastCompleted, lastFailed) = previous.GetValueOrDefault(id);
                live.TryGetValue(id, out var node);

                var point = new TrafficPoint(
                    Math.Max(0, completed - lastCompleted),
                    Math.Max(0, failed - lastFailed),
                    node?.InFlight ?? 0,
                    node?.MaxConcurrency);

                // A node that is gone and did nothing leaves no point: absence stays absence (28 D5).
                if (node is not null || point.Completed > 0 || point.Failed > 0)
                {
                    points[id] = point;
                }
            }

            previous = counters;
            samples.Enqueue(new TrafficSample(now, points));

            while (samples.Count > Capacity && samples.TryDequeue(out _))
            {
            }
        }
    }

    /// <summary>The samples at or after <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<TrafficSample> Since(DateTimeOffset since) => samples.Where(s => s.At >= since).ToArray();

    public TrafficNode? Known(string nodeId) => known.GetValueOrDefault(nodeId);

    public IReadOnlyCollection<TrafficNode> AllKnown() => known.Values.ToArray();

    /// <summary>Requests finished and failed by one node since <paramref name="since"/>.</summary>
    public (long Completed, long Failed) Totals(string nodeId, DateTimeOffset since)
    {
        long completed = 0, failed = 0;

        foreach (var sample in Since(since))
        {
            if (sample.Nodes.TryGetValue(nodeId, out var point))
            {
                completed += point.Completed;
                failed += point.Failed;
            }
        }

        return (completed, failed);
    }
}
