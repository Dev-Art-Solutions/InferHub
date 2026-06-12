namespace InferHub.Coordinator.Services;

public sealed class NodeReaper(
    INodeRegistry registry,
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
