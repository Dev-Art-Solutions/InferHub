using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Resources;

/// <summary>
/// Polls this box's own CPU and GPU utilization and decides whether it is over the operator's own
/// ceiling (phase 82). Registered when either the soft cap or <c>HardCpuCapPercent</c> is configured —
/// <see cref="NoResourceGovernor"/> stands in otherwise, so nothing downstream branches on the feature
/// existing (the same shape as <c>IBackendSupervisor</c>/<c>NoBackendSupervisor</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This service only ever answers one question: would placing more work here push this box over
/// its own limit.</b> It never kills, throttles or renices anything itself — that is
/// <c>Node:ResourceLimits:HardCpuCapPercent</c>'s job (D6), a different mechanism entirely. Tripping
/// withdraws the node from new placement the same way an unhealthy backend does (phase 69's
/// <c>Heartbeat</c> field and <c>NodeRegistry.FindNodesWithModel</c>); a request already running is
/// never touched.
/// </para>
/// <para>
/// <b>Reads <see cref="IOptionsMonitor{T}"/> fresh on every tick rather than caching at construction
/// (D7).</b> That is what makes the local admin page's writes to <c>node.local.json</c> reach this
/// decision within one <see cref="ResourceLimitOptions.PollInterval"/> with no restart —
/// <see cref="ResourceGovernorLogic"/> takes the current numbers as arguments to <c>Apply</c> for
/// exactly this reason. <c>HardCpuCapPercent</c> is the one exception, deliberately: it is applied
/// once, in <see cref="StartAsync"/>, because re-configuring a Windows Job Object's rate after
/// processes are already running inside it is not something this phase attempts.
/// </para>
/// </remarks>
internal sealed class ResourceMonitor : IResourceGovernor, IHostedService, IDisposable
{
    private readonly IOptionsMonitor<NodeOptions> nodeOptions;
    private readonly TimeProvider time;
    private readonly ILogger<ResourceMonitor> logger;
    private readonly CpuUsageProbe cpu = new();
    private readonly NvmlProbe gpu = new();
    private readonly ResourceGovernorLogic logic = new();
    private CancellationTokenSource? lifetime;
    private bool gpuInitialized;

    public ResourceMonitor(
        IOptionsMonitor<NodeOptions> nodeOptions,
        TimeProvider time,
        ILogger<ResourceMonitor> logger)
    {
        this.nodeOptions = nodeOptions;
        this.time = time;
        this.logger = logger;
    }

    public ResourceSnapshot Current { get; private set; }

    public bool IsThrottled => logic.IsThrottled;

    public string? Reason => logic.Reason;

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = nodeOptions.CurrentValue.ResourceLimits;

        // D6, independent of the soft cap above, and applied once: a deployment may set
        // HardCpuCapPercent with no MaxCpuPercent/MaxGpuPercent at all, and changing the rate on a
        // job object already holding running processes is not something this phase attempts.
        HardCpuCap.Configure(options.HardCpuCapPercent, logger);

        logger.LogInformation(
            "Node:ResourceLimits is on: MaxCpuPercent={Cpu}, MaxGpuPercent={Gpu}, polling every {Interval}. This node withdraws from new placement after {Sustained} consecutive over-cap poll(s) and returns after {Recover}. Both are editable live, with no restart, via node.local.json or (with LocalApi:Enabled=true) /api/admin/resource-limits.",
            options.MaxCpuPercent?.ToString() ?? "unset",
            options.MaxGpuPercent?.ToString() ?? "unset",
            options.PollInterval,
            options.SustainedPolls,
            options.RecoverPolls);

        lifetime = new CancellationTokenSource();

        // Fire-and-forget by design, like OllamaSupervisor's own loop: StartAsync must return
        // promptly, and this loop runs for the lifetime of the process.
        _ = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // A throwaway first sample: CpuUsageProbe reports a delta, and there is nothing to diff
        // against yet. Admitting on an unmeasured first tick is the safe default — see
        // VramBudget's own reasoning for a recipe with no declared figure.
        cpu.Read();

        while (!cancellationToken.IsCancellationRequested)
        {
            var options = nodeOptions.CurrentValue.ResourceLimits;

            try
            {
                await Task.Delay(options.PollInterval, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Re-read after the delay too: a live edit to MaxGpuPercent (off -> on) during the wait
            // must be seen by the branch below on the very tick it takes effect.
            options = nodeOptions.CurrentValue.ResourceLimits;

            if (options.MaxGpuPercent is not null && !gpuInitialized)
            {
                gpuInitialized = true;
                gpu.Initialize();

                if (!gpu.Available)
                {
                    logger.LogWarning(
                        "{Key} is set but no NVML device is visible to this process (NVIDIA driver, Linux only today); the GPU half of this node's resource cap will never trip.",
                        $"{NodeOptions.SectionName}:ResourceLimits:{nameof(ResourceLimitOptions.MaxGpuPercent)}");
                }
            }

            var snapshot = new ResourceSnapshot(
                cpu.Read(),
                options.MaxGpuPercent is null ? null : gpu.Read());

            Current = snapshot;

            var changed = logic.Apply(
                snapshot,
                options.MaxCpuPercent,
                options.MaxGpuPercent,
                options.SustainedPolls,
                options.RecoverPolls);

            if (!changed)
            {
                continue;
            }

            if (logic.IsThrottled)
            {
                logger.LogWarning(
                    "Resource cap tripped: {Reason}. This node will not accept new placement until it recovers.",
                    logic.Reason);
            }
            else
            {
                logger.LogInformation("Resource cap cleared; this node is routable again.");
            }

            Changed?.Invoke();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lifetime?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lifetime?.Dispose();
        gpu.Dispose();
    }
}
