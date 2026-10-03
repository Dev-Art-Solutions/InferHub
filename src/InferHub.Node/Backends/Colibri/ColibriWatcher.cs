using InferHub.Node.Backends.Supervision;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// Asks colibri's gateway <c>GET /health</c> every <c>ProbeInterval</c> and declares the answer on
/// 69's threshold (93 D4). <b>It never restarts anything</b>: a 744B prefill on a disk-streaming CPU
/// path runs at a few tokens a second and looks exactly like a wedge from outside, and killing it
/// throws away the KV the next attempt needs. A launched engine that <em>exits</em> is
/// <see cref="ColibriServe"/>'s, which is a different fact.
/// </summary>
public sealed class ColibriWatcher(
    IHttpClientFactory httpClientFactory,
    IOptions<ColibriOptions> options,
    IOptions<UpstreamBackendOptions> upstream,
    TimeProvider time,
    ILogger<ColibriWatcher> logger) : BackgroundService, IBackendSupervisor
{
    public const string HttpClientName = "colibri-health-probe";

    private const int NotProbed = -1;

    private readonly ColibriOptions options = options.Value;

    private int healthCode = NotProbed;

    private int consecutiveFailures;

    public bool IsSupervising => true;

    public BackendHealth? Health
        => Volatile.Read(ref healthCode) is var code and not NotProbed ? (BackendHealth)code : null;

    public event Action? Recovered;

    /// <summary>Never raised: this watcher restarts nothing (D4).</summary>
    public event Action<BackendHealth>? Restarting
    {
        add { }
        remove { }
    }

    /// <summary>
    /// <c>/health</c> lives at the gateway's root, not under <c>/v1</c>, so it is resolved from the
    /// configured base URL rather than appended to it.
    /// </summary>
    public static Uri HealthUri(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');

        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^3];
        }

        return new Uri(root + "/health");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ProbeInterval, time);

        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "colibri health probe failed unexpectedly");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>One probe and its verdict. Internal so the tests can drive it without a loop.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var baseUrl = upstream.Value.ResolvedBaseUrl(BackendOptions.Colibri)!;
        var health = await OllamaProbe.ClassifyAsync(
            httpClientFactory.CreateClient(HttpClientName),
            HealthUri(baseUrl).ToString(),
            cancellationToken);

        if (health is BackendHealth.Healthy)
        {
            var failedBefore = consecutiveFailures > 0;
            consecutiveFailures = 0;

            var previous = Interlocked.Exchange(ref healthCode, (int)BackendHealth.Healthy);
            var wasDeclared = previous is not NotProbed and not (int)BackendHealth.Healthy;

            // The model report is what routes this node, so an engine that has just come up is
            // pushed rather than waited out on the 60 s refresh — 69's reason, widened by one case
            // found on a real boot: a launched engine is still loading when the node registers, its
            // first listing is "could not ask", and a failure below the threshold never became an
            // outage to recover from. The node sat unroutable for a minute after every start.
            if (wasDeclared || failedBefore)
            {
                if (wasDeclared)
                {
                    logger.LogInformation("colibri at {BaseUrl} is healthy again.", baseUrl);
                }

                Recovered?.Invoke();
            }

            return;
        }

        consecutiveFailures++;

        if (consecutiveFailures < options.UnhealthyThreshold)
        {
            return;
        }

        if (Interlocked.Exchange(ref healthCode, (int)health) != (int)health)
        {
            logger.LogWarning(
                "colibri at {BaseUrl} is {Health} after {Failures} consecutive failed probes.",
                baseUrl,
                health,
                consecutiveFailures);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
