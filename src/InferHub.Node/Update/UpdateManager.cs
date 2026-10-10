using System.Text.Json;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Update;

/// <summary>The answer to a check or an apply: whether it went ahead, and the sentence either way.</summary>
public sealed record UpdateOutcome(bool Accepted, string Message);

/// <summary>
/// The node's own version, the newest release it could move to, and who may move it there (phase 101).
/// </summary>
/// <remarks>
/// <para>
/// <b>One flag decides who applies</b> (D2): with <c>Update:Auto</c> this loop applies a release once the node
/// is idle; without it the release is only reported, and an admin applies it from the hub
/// (<c>Update:AllowFromHub</c>) or on the box (<c>InferHub.Node.Service.exe update</c>).
/// </para>
/// <para>
/// <b>Applying ends this process</b> (D4): the setup stops the service, replaces it and starts it again. So
/// what the update came to is learned on the next boot, from a marker written just before the setup was
/// started — the running version either is the target or it is not.
/// </para>
/// </remarks>
public sealed class UpdateManager(
    IOptions<UpdateOptions> optionsAccessor,
    IReleaseFeed feed,
    UpdateDownloader downloader,
    IUpdateApplier applier,
    string dataDirectory,
    Func<int> inFlight,
    TimeProvider time,
    ILogger<UpdateManager> logger) : BackgroundService
{
    internal const string MarkerName = "pending.json";

    /// <summary>A setup that was started and did not stop this process within this long has failed.</summary>
    internal static readonly TimeSpan LaunchGrace = TimeSpan.FromMinutes(10);

    private readonly UpdateOptions options = optionsAccessor.Value;
    private readonly Lock gate = new();
    private string phase = NodeUpdatePhase.Unknown;
    private UpdateRelease? available;
    private DateTimeOffset? lastChecked;
    private string? lastError;
    private string? lastUpdate;
    private int busy;
    private bool warnedCannotApply;

    /// <summary>Raised whenever the state changes, so the hub hears it now rather than at the next report.</summary>
    public event Action? Changed;

    public UpdateOptions Options => options;

    /// <summary>Where setups are downloaded to and the marker is kept: <c>&lt;Node:DataDirectory&gt;/updates</c>.</summary>
    public string UpdatesDirectory { get; } = Path.Combine(dataDirectory, "updates");

    public NodeUpdateState State(string nodeId)
    {
        lock (gate)
        {
            return new NodeUpdateState(
                nodeId,
                NodeVersion.Current,
                available is null ? null : NodeVersion.Format(available.Version),
                phase,
                options.Check,
                options.Auto,
                options.AllowFromHub,
                applier.CanApply,
                applier.CanApply ? null : applier.WhyNot,
                lastChecked,
                lastError,
                lastUpdate,
                available?.PageUrl,
                time.GetUtcNow());
        }
    }

    /// <summary>Asks the feed now. A failure is recorded and reported, never thrown.</summary>
    public async Task<UpdateOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        Set(NodeUpdatePhase.Checking);

        try
        {
            var release = await feed.NewestAboveAsync(NodeVersion.Parsed, cancellationToken);

            lock (gate)
            {
                available = release;
                lastChecked = time.GetUtcNow();
                lastError = null;
                phase = release is null ? NodeUpdatePhase.UpToDate : NodeUpdatePhase.Available;
            }

            Changed?.Invoke();

            if (release is null)
            {
                return new UpdateOutcome(true, $"{NodeVersion.Current} is the newest release");
            }

            logger.LogInformation(
                "InferHub {Available} is available (this node runs {Current}): {Page}",
                NodeVersion.Format(release.Version),
                NodeVersion.Current,
                release.PageUrl);
            return new UpdateOutcome(true, $"{NodeVersion.Format(release.Version)} is available");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = Sentence(ex);

            lock (gate)
            {
                lastChecked = time.GetUtcNow();
                lastError = $"could not check for updates: {message}";
                phase = NodeUpdatePhase.Failed;
            }

            Changed?.Invoke();
            logger.LogWarning("Could not check for InferHub updates: {Reason}", message);
            return new UpdateOutcome(false, $"could not check for updates: {message}");
        }
    }

    /// <summary>
    /// Downloads, verifies and starts the newest release's setup. <paramref name="fromHub"/> is refused unless
    /// <c>Update:AllowFromHub</c> — the node is the boundary (43 D1), whatever the hub decided.
    /// </summary>
    public async Task<UpdateOutcome> ApplyAsync(string requestedBy, bool fromHub, CancellationToken cancellationToken)
    {
        if (fromHub && !options.AllowFromHub)
        {
            return new UpdateOutcome(false, "Update:AllowFromHub is off on this node: its operator updates it by hand");
        }

        if (!applier.CanApply)
        {
            return new UpdateOutcome(false, applier.WhyNot ?? "this node cannot apply updates");
        }

        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            return new UpdateOutcome(false, "an update is already being applied");
        }

        var launched = false;

        try
        {
            UpdateRelease? release;

            lock (gate)
            {
                release = available;
            }

            if (release is null)
            {
                var check = await CheckAsync(cancellationToken);

                if (!check.Accepted)
                {
                    return check;
                }

                lock (gate)
                {
                    release = available;
                }

                if (release is null)
                {
                    return new UpdateOutcome(false, check.Message);
                }
            }

            var target = NodeVersion.Format(release.Version);
            logger.LogInformation("Updating this node {Current} → {Target} (requested by {By})", NodeVersion.Current, target, requestedBy);
            Set(NodeUpdatePhase.Downloading);

            var setup = await downloader.DownloadAsync(release, UpdatesDirectory, cancellationToken);
            var log = Path.Combine(UpdatesDirectory, $"setup-{target}.log");

            WriteMarker(new Marker(NodeVersion.Current, target, log, time.GetUtcNow(), requestedBy));
            Set(NodeUpdatePhase.Applying);

            await applier.LaunchAsync(setup, log, cancellationToken);
            launched = true;
            logger.LogInformation("Started the {Target} setup; this service will stop and come back on the new version. Setup log: {Log}", target, log);

            // The setup stops this process. If it is still here long after, the setup failed before it got
            // that far — say so rather than reading "applying" forever.
            _ = WatchLaunchAsync(target, log);
            return new UpdateOutcome(true, $"updating to {target}; the node restarts when the setup finishes");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = Sentence(ex);
            DeleteMarker();

            lock (gate)
            {
                lastError = $"could not apply the update: {message}";
                phase = NodeUpdatePhase.Failed;
            }

            Changed?.Invoke();
            logger.LogError("Could not apply the InferHub update: {Reason}", message);
            return new UpdateOutcome(false, $"could not apply the update: {message}");
        }
        finally
        {
            if (!launched)
            {
                Volatile.Write(ref busy, 0);
            }
        }
    }

    /// <summary>
    /// A command this node would not carry out — reported as <c>lastError</c> without changing the phase, so
    /// the console shows the node's own reason next to the button that was pressed.
    /// </summary>
    public void Refused(string message)
    {
        lock (gate)
        {
            lastError = message;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// What the last update came to, from the marker the apply wrote before its setup stopped this process.
    /// Read once at start; the marker is deleted either way, so a failure is reported, not retried in a loop.
    /// </summary>
    public void ReadMarker()
    {
        var path = Path.Combine(UpdatesDirectory, MarkerName);

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));

            if (marker is null)
            {
                return;
            }

            lock (gate)
            {
                if (string.Equals(marker.To, NodeVersion.Current, StringComparison.OrdinalIgnoreCase))
                {
                    lastUpdate = $"updated {marker.From} → {marker.To} at {marker.AtUtc:u} (requested by {marker.By})";
                }
                else
                {
                    lastUpdate = $"the {marker.To} setup ran at {marker.AtUtc:u} but this node still runs {NodeVersion.Current}";
                    lastError = $"{lastUpdate}; see {marker.Log}";
                    phase = NodeUpdatePhase.Failed;
                }
            }

            if (lastError is null)
            {
                logger.LogInformation("This node was {LastUpdate}", lastUpdate);
            }
            else
            {
                logger.LogWarning("The last update did not take: {Error}", lastError);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the update marker {Path}", path);
        }
        finally
        {
            DeleteMarker();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ReadMarker();

        if (!options.Check)
        {
            return;
        }

        try
        {
            await Task.Delay(options.FirstCheckDelay, time, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);

                if (options.Auto && HasAvailable())
                {
                    if (!applier.CanApply)
                    {
                        if (!warnedCannotApply)
                        {
                            warnedCannotApply = true;
                            logger.LogWarning("Update:Auto is on but this node cannot apply updates: {Reason}", applier.WhyNot);
                        }
                    }
                    else
                    {
                        await WaitForIdleAsync(stoppingToken);
                        await ApplyAsync("Update:Auto", fromHub: false, stoppingToken);
                    }
                }

                await Task.Delay(options.Interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>D4: idle, or <c>Update:DrainTimeout</c> — a node that is never idle still updates.</summary>
    internal async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + options.DrainTimeout;
        var logged = false;

        while (inFlight() > 0 && time.GetUtcNow() < deadline)
        {
            if (!logged)
            {
                logged = true;
                logger.LogInformation("An update is ready; waiting up to {Timeout} for {InFlight} job(s) to finish", options.DrainTimeout, inFlight());
            }

            await Task.Delay(TimeSpan.FromSeconds(5), time, cancellationToken);
        }
    }

    private async Task WatchLaunchAsync(string target, string log)
    {
        try
        {
            await Task.Delay(LaunchGrace, time, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DeleteMarker();

        lock (gate)
        {
            lastError = $"the {target} setup was started {LaunchGrace.TotalMinutes:0} minutes ago and this node still runs {NodeVersion.Current}; see {log}";
            phase = NodeUpdatePhase.Failed;
        }

        Volatile.Write(ref busy, 0);
        Changed?.Invoke();
        logger.LogError("The update did not take: {Error}", lastError);
    }

    private bool HasAvailable()
    {
        lock (gate)
        {
            return available is not null;
        }
    }

    private void Set(string next)
    {
        lock (gate)
        {
            phase = next;
        }

        Changed?.Invoke();
    }

    private void WriteMarker(Marker marker)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        File.WriteAllText(Path.Combine(UpdatesDirectory, MarkerName), JsonSerializer.Serialize(marker));
    }

    private void DeleteMarker()
    {
        try
        {
            File.Delete(Path.Combine(UpdatesDirectory, MarkerName));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete the update marker");
        }
    }

    private static string Sentence(Exception ex) => ex switch
    {
        UpdateException update => update.Message,
        HttpRequestException http => $"the release feed could not be reached ({http.Message})",
        TaskCanceledException => "the request timed out",
        _ => ex.Message,
    };

    internal sealed record Marker(string From, string To, string Log, DateTimeOffset AtUtc, string By);
}
