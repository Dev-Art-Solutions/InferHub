using InferHub.Coordinator.Observability;

namespace InferHub.Coordinator.Services;

public sealed class NodeReaper(
    INodeRegistry registry,
    IDispatcher dispatcher,
    Metrics metrics,
    IConfiguration configuration,
    ILogger<NodeReaper> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = GetSeconds("NodeRegistry:TimeoutSeconds", DefaultTimeout);
        var interval = GetSeconds("NodeRegistry:ReaperIntervalSeconds", DefaultInterval);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = now - timeout;

            foreach (var node in registry.EvictStale(cutoff, now))
            {
                logger.LogWarning(
                    "Evicted stale node {NodeId} ({NodeName}) after {AgeSeconds:F1}s without heartbeat",
                    node.NodeId,
                    node.Name,
                    node.AgeSeconds);

                dispatcher.FailForConnection(
                    node.ConnectionId,
                    new TimeoutException($"node '{node.NodeId}' missed its heartbeat"));
                // Affinity is not forgotten on eviction (phase 30): an evicted node that re-registers
                // with the same node id resumes its warm conversations, and the sliding window bounds
                // the map for one that never returns. Only an explicit deregister forgets a node.
                metrics.RecordNodeEvicted();
            }
        }
    }

    private TimeSpan GetSeconds(string key, TimeSpan fallback)
    {
        var seconds = configuration.GetValue<double?>(key);

        return seconds is > 0
            ? TimeSpan.FromSeconds(seconds.Value)
            : fallback;
    }
}
