using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Tools;

/// <summary>
/// The real runtime: one <see cref="ToolWorkerPool"/> per allowed manifest, plus the maintenance
/// loop that retires idle workers and probes pools that have given up.
/// </summary>
/// <remarks>
/// <b>Nothing is discovered-and-run</b> (D2). Every manifest in the directory is loaded and
/// reported; only the ones named in <c>Tools:Allowed</c> get a pool. A manifest that is present but
/// not allowed is logged once at startup with its id, because "I put the file there and nothing
/// happened" is otherwise a silent afternoon.
/// </remarks>
internal sealed class ProcessToolRuntime : IToolRuntime, IHostedService, IAsyncDisposable
{
    private readonly ToolOptions options;
    private readonly TimeProvider time;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<ProcessToolRuntime> logger;
    private readonly List<ToolWorkerPool> pools = new();
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>
    /// Manifest ids that were loaded and never started because <c>Tools:Allowed</c> does not name
    /// them (D2). Kept so the hub can be told, rather than only the log on this box.
    /// </summary>
    private readonly List<string> notAllowed = new();

    private Task? maintenance;

    public ProcessToolRuntime(
        IOptions<ToolOptions> toolOptions,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ILogger<ProcessToolRuntime> logger)
    {
        options = toolOptions.Value;
        this.time = time;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
    }

    public bool Enabled => true;

    public IReadOnlyList<NodeCapability> Capabilities
    {
        get
        {
            // Merged per kind rather than per pool: the node declares "I can transcribe these
            // models", not "tool A can transcribe these and tool B those", because the routing key
            // is (capability, model) and which process serves it is the node's business.
            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            lock (pools)
            {
                foreach (var capability in pools.SelectMany(pool => pool.Capabilities))
                {
                    if (!merged.TryGetValue(capability.Kind, out var models))
                    {
                        models = new List<string>();
                        merged[capability.Kind] = models;
                    }

                    foreach (var model in capability.Models)
                    {
                        if (!models.Contains(model, StringComparer.OrdinalIgnoreCase))
                        {
                            models.Add(model);
                        }
                    }
                }
            }

            return merged
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new NodeCapability(
                    pair.Key,
                    pair.Value.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToArray()))
                .ToArray();
        }
    }

    public event Action? CapabilitiesChanged;

    /// <summary>
    /// Phase 45. Every manifest on the box, started or not, so the four ways a tool can fail to serve
    /// — not allowed, suspended by a profile, given up, or simply running — are four different
    /// answers at the hub rather than one absence.
    /// </summary>
    public NodeToolState State(string nodeId)
    {
        ToolWorkerPool[] snapshot;
        string[] refused;

        lock (pools)
        {
            snapshot = pools.ToArray();
            refused = notAllowed.ToArray();
        }

        var tools = snapshot
            .Select(pool => pool.Report())
            .Concat(refused.Select(id => new NodeToolInfo(
                id,
                Allowed: false,
                NodeToolInfo.NotAllowed,
                Array.Empty<NodeCapability>(),
                MaxWorkers: 0,
                Workers: 0,
                Busy: 0,
                Requests: 0,
                Failures: 0,
                LastError: null,
                LastErrorAtUtc: null)))
            .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NodeToolState(nodeId, Enabled: true, tools, DateTimeOffset.UtcNow);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = options.ResolvedManifestDirectory();
        var manifests = ToolManifestLoader.LoadDirectory(directory, logger);

        foreach (var manifest in manifests)
        {
            if (!options.IsAllowed(manifest.Id))
            {
                logger.LogWarning(
                    "Tool manifest '{ToolId}' was loaded from {Directory} but is not in {Key}, so it will not be started. Add its id to that list to run it.",
                    manifest.Id,
                    directory,
                    $"{ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)}");

                lock (pools)
                {
                    notAllowed.Add(manifest.Id);
                }

                continue;
            }

            var pool = new ToolWorkerPool(
                manifest,
                options,
                time,
                loggerFactory.CreateLogger($"InferHub.Node.Tools.{manifest.Id}"));

            pool.CapabilitiesChanged += RaiseCapabilitiesChanged;

            lock (pools)
            {
                pools.Add(pool);
            }

            await pool.StartAsync(cancellationToken);
        }

        var missing = options.Allowed
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => manifests.All(m => !string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (missing.Length > 0)
        {
            // Allowing a tool that is not there is how a deployment ends up believing it can
            // transcribe. It is a warning rather than a failure because the ordinary cause is a
            // volume that has not been mounted yet, and a node that refuses to boot over it is
            // worse than one that says so.
            logger.LogWarning(
                "{Key} names {Missing}, but no manifest with that id was found in {Directory}.",
                $"{ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)}",
                string.Join(", ", missing.Select(id => $"'{id.Trim()}'")),
                directory);
        }

        logger.LogInformation(
            "Tool runtime is on: {Started} of {Loaded} manifest(s) started from {Directory}, scratch at {Scratch}. Workers run as this node's user with this node's filesystem — this is process isolation, not a sandbox.",
            pools.Count,
            manifests.Count,
            directory,
            options.ResolvedScratchDirectory());

        maintenance = Task.Run(() => MaintainAsync(lifetime.Token), CancellationToken.None);
        RaiseCapabilitiesChanged();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifetime.CancelAsync();

        if (maintenance is not null)
        {
            try
            {
                await maintenance.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposePoolsAsync();
    }

    public async Task<ToolWorkerLease> AcquireAsync(
        string capability,
        string model,
        CancellationToken cancellationToken)
    {
        ToolWorkerPool? pool;

        lock (pools)
        {
            // Matched against the pool's *live* capabilities, not the manifest's: a pool that has
            // given up must read as "this node does not provide it" rather than as a queue.
            // A suspended pool is excluded from both passes (phase 43): a tool the coordinator
            // switched off must read as "this node does not provide it", which is what it now is.
            pool = pools.FirstOrDefault(p => !p.Suspended && Provides(p.Capabilities, capability, model))
                ?? pools.FirstOrDefault(p => !p.Suspended && p.Manifest.Provides(capability, model));
        }

        if (pool is null)
        {
            throw new ToolNotProvidedException(capability, model);
        }

        return await pool.AcquireAsync(cancellationToken);
    }

    /// <summary>
    /// Phase 43. The set has already been through <c>NodeProfileClamp</c>, so it is a subset of
    /// <c>Tools:Allowed</c> by construction — nothing here can start a tool the operator did not
    /// grant, because nothing here can create a pool.
    /// </summary>
    public async Task SetDisabledToolsAsync(
        IReadOnlyCollection<string> toolIds,
        CancellationToken cancellationToken)
    {
        ToolWorkerPool[] snapshot;

        lock (pools)
        {
            snapshot = pools.ToArray();
        }

        foreach (var pool in snapshot)
        {
            var shouldSuspend = toolIds.Any(id =>
                string.Equals(id?.Trim(), pool.Manifest.Id, StringComparison.OrdinalIgnoreCase));

            if (shouldSuspend)
            {
                await pool.SuspendAsync();
            }
            else
            {
                await pool.ResumeAsync(cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        await DisposePoolsAsync();
    }

    private static bool Provides(IReadOnlyList<NodeCapability> capabilities, string capability, string model) =>
        capabilities.Any(c =>
            string.Equals(c.Kind, capability, StringComparison.OrdinalIgnoreCase)
            && c.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)));

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.MaintenanceInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }

                ToolWorkerPool[] snapshot;

                lock (pools)
                {
                    snapshot = pools.ToArray();
                }

                foreach (var pool in snapshot)
                {
                    await pool.MaintainAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tool maintenance pass failed");
            }
        }
    }

    private async Task DisposePoolsAsync()
    {
        ToolWorkerPool[] snapshot;

        lock (pools)
        {
            snapshot = pools.ToArray();
            pools.Clear();
        }

        foreach (var pool in snapshot)
        {
            pool.CapabilitiesChanged -= RaiseCapabilitiesChanged;
            await pool.DisposeAsync();
        }
    }

    private void RaiseCapabilitiesChanged()
    {
        try
        {
            CapabilitiesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A tool capability subscriber threw");
        }
    }
}
