using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Control;

/// <summary>
/// Whether this node takes new network jobs now (phase 113) — from its owner's <c>Node:Sharing:Mode</c>, read live,
/// and, in <c>idle</c> mode, from what <see cref="NodeActivity"/> says this machine itself is running. Phase 116 adds
/// the owner's terms: the hours it shares (<c>Node:Sharing:Hours</c>) and how much network work it runs a day
/// (<c>Node:Sharing:DailyNetworkMinutes</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The node decides; the hub is told</b> (113 D1). <see cref="Changed"/> is what the mesh connection reports
/// through the mailbox. Pausing never cancels a running network job (D3): only new ones wait — so a job that starts
/// a minute before the hours close, or with a minute of the allowance left, runs to its end (116 D3).
/// </para>
/// <para>
/// <b>idle</b> pauses before the first local job runs — <see cref="NodeActivity.Changed"/> is raised from
/// <see cref="NodeActivity.Begin"/>, ahead of the work — and resumes <c>IdleSeconds</c> after the last one ends.
/// </para>
/// <para>
/// <b>The allowance survives a restart</b> (116 D4): the seconds used today are written beside the control token,
/// so restarting the node is not a way to hand the network a second day.
/// </para>
/// </remarks>
public sealed class NodeSharing : IHostedService, IDisposable
{
    public const string UsageFileName = "sharing-usage.json";

    private readonly IOptionsMonitor<NodeOptions> options;
    private readonly NodeActivity activity;
    private readonly TimeProvider time;
    private readonly ILogger<NodeSharing> logger;
    private readonly string? usageFile;
    private readonly object gate = new();
    private IDisposable? optionsSubscription;
    private ITimer? resumeTimer;
    private DateTimeOffset? lastLocalEnd;
    private NodeSharingState state;
    private HoursCache? hoursCache;
    private DateOnly usageDay;
    private double usedSeconds;

    public NodeSharing(IOptionsMonitor<NodeOptions> options, NodeActivity activity, ILogger<NodeSharing> logger, TimeProvider? time = null, string? usageFile = null)
    {
        this.options = options;
        this.activity = activity;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        this.usageFile = usageFile;
        state = new NodeSharingState(string.Empty, Mode, false, null, this.time.GetUtcNow(), 0, 0);
        LoadUsage();
        activity.Changed += OnActivity;
        optionsSubscription = options.OnChange(_ => Recompute());
        Recompute();
    }

    /// <summary>Raised when <see cref="Paused"/> or the mode changes — not on every job.</summary>
    public event Action<NodeSharingState>? Changed;

    public string Mode => (options.CurrentValue.Sharing.Mode ?? SharingOptions.Always).Trim().ToLowerInvariant();

    public bool Paused
    {
        get
        {
            lock (gate)
            {
                return state.Paused;
            }
        }
    }

    /// <summary>The hours as they are configured now; <see cref="SharingHours.Always"/> when none (or, never in a running node, invalid).</summary>
    public SharingHours Hours
    {
        get
        {
            var sharing = options.CurrentValue.Sharing;
            var cached = hoursCache;

            if (cached is not null && ReferenceEquals(cached.Source, sharing))
            {
                return cached.Hours;
            }

            var hours = SharingHours.Parse(sharing.Hours, sharing.TimeZone, out _);
            hoursCache = new HoursCache(sharing, hours);
            return hours;
        }
    }

    /// <summary>The state as the hub and the control API see it, with the counts as they are now.</summary>
    public NodeSharingState State(string nodeId)
    {
        lock (gate)
        {
            return state with
            {
                NodeId = nodeId,
                LocalJobs = activity.RunningCount(NodeActivity.Local),
                NetworkJobs = activity.RunningCount(NodeActivity.Mesh)
            };
        }
    }

    /// <summary>Today's network work against the allowance — what the status and the app show (116).</summary>
    public SharingAllowance Allowance()
    {
        var now = time.GetUtcNow();
        var minutes = Math.Max(0, options.CurrentValue.Sharing.DailyNetworkMinutes);

        lock (gate)
        {
            var used = UsedSecondsLocked(now);
            return new SharingAllowance(
                minutes,
                Math.Round(used, 1),
                minutes == 0 ? null : Math.Round(Math.Max(0, minutes * 60.0 - used), 1),
                NextDayStart(now));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Nothing to save: every finished network job was written as it ended, and one cut short by the stop is not
        // counted — a crash costs the network a few minutes, never the owner a second day.
        resumeTimer?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        activity.Changed -= OnActivity;
        optionsSubscription?.Dispose();
        resumeTimer?.Dispose();
    }

    private void OnActivity(ActivityEntry entry)
    {
        if (entry.EndedAt is { } ended)
        {
            lock (gate)
            {
                if (entry.Source == NodeActivity.Local)
                {
                    lastLocalEnd = ended;
                }
                else
                {
                    RollDayLocked(ended);
                    var from = entry.StartedAt > DayStart(ended) ? entry.StartedAt : DayStart(ended);
                    usedSeconds += Math.Max(0, (ended - from).TotalSeconds);
                    SaveUsageLocked();
                }
            }
        }

        Recompute();
    }

    internal void Recompute()
    {
        var mode = Mode;
        var sharingOptions = options.CurrentValue.Sharing;
        var local = activity.RunningCount(NodeActivity.Local);
        var network = activity.RunningCount(NodeActivity.Mesh);
        var now = time.GetUtcNow();
        var idleFor = TimeSpan.FromSeconds(Math.Max(0, sharingOptions.IdleSeconds));
        var hours = Hours;
        var allowance = TimeSpan.FromMinutes(Math.Max(0, sharingOptions.DailyNetworkMinutes));
        var open = hours.IsOpen(now, out var hoursChangeAt);
        bool paused;
        string? reason;
        DateTimeOffset? resumesAt = null;
        var wakes = new List<DateTimeOffset>();

        if (hoursChangeAt is { } change)
        {
            wakes.Add(change);
        }

        lock (gate)
        {
            var used = TimeSpan.FromSeconds(UsedSecondsLocked(now));
            var spent = allowance > TimeSpan.Zero && used >= allowance;

            if (allowance > TimeSpan.Zero)
            {
                wakes.Add(NextDayStart(now));

                // With network jobs running the allowance runs down on its own: look again when it would be gone.
                if (!spent && network > 0)
                {
                    wakes.Add(now + (allowance - used) / network);
                }
            }

            switch (mode)
            {
                case SharingOptions.Paused:
                    paused = true;
                    reason = "paused by its owner";
                    break;

                case SharingOptions.Idle when local > 0:
                    paused = true;
                    reason = $"its owner is using it ({local} local job{(local == 1 ? "" : "s")})";
                    break;

                case SharingOptions.Idle when lastLocalEnd is { } end && now - end < idleFor:
                    paused = true;
                    resumesAt = end + idleFor;
                    wakes.Add(resumesAt.Value);
                    reason = $"its owner used it {Math.Round((now - end).TotalSeconds)} s ago; sharing resumes after {idleFor.TotalSeconds:0} s idle";
                    break;

                case not SharingOptions.Paused when !open:
                    paused = true;
                    resumesAt = hoursChangeAt;
                    reason = hoursChangeAt is { } opens
                        ? $"outside its sharing hours; shares again {hours.Describe(opens, now)}"
                        : "outside its sharing hours";
                    break;

                case not SharingOptions.Paused when spent:
                    paused = true;
                    resumesAt = NextDayStart(now);
                    reason = $"today's network allowance of {allowance.TotalMinutes:0} min is used; shares again {hours.Describe(resumesAt.Value, now)}";
                    break;

                default:
                    paused = false;
                    reason = null;
                    break;
            }

            ScheduleWake(wakes.Where(w => w > now).DefaultIfEmpty().Min());

            if (state.Paused == paused && state.Mode == mode && state.Reason == reason)
            {
                return;
            }

            var since = state.Paused == paused && state.Mode == mode ? state.SinceUtc : now;
            state = state with { Mode = mode, Paused = paused, Reason = reason, SinceUtc = since, ResumesAtUtc = resumesAt };
        }

        if (paused)
        {
            logger.LogInformation("Sharing paused ({Mode}): {Reason}.", mode, reason);
        }
        else
        {
            logger.LogInformation("Sharing with the network ({Mode}).", mode);
        }

        activity.Publish("sharing.changed", State(string.Empty));
        Changed?.Invoke(State(string.Empty));
    }

    private void ScheduleWake(DateTimeOffset at)
    {
        resumeTimer?.Dispose();
        var delay = at - time.GetUtcNow();
        resumeTimer = at != default && delay > TimeSpan.Zero
            ? time.CreateTimer(_ => Recompute(), null, delay + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan)
            : null;
    }

    // ---- the day's allowance (116 D4) ----

    private DateTimeOffset DayStart(DateTimeOffset at)
    {
        var zone = Hours.Zone;
        var midnight = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(at, zone).Date, DateTimeKind.Unspecified);

        while (zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);
    }

    private DateTimeOffset NextDayStart(DateTimeOffset at) => DayStart(DayStart(at) + TimeSpan.FromHours(26));

    private DateOnly Today(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Hours.Zone).Date);

    private void RollDayLocked(DateTimeOffset now)
    {
        var today = Today(now);

        if (usageDay != today)
        {
            usageDay = today;
            usedSeconds = 0;
        }
    }

    /// <summary>Finished network work today, plus today's part of what is running now.</summary>
    private double UsedSecondsLocked(DateTimeOffset now)
    {
        RollDayLocked(now);
        var dayStart = DayStart(now);
        var running = activity.Running
            .Where(e => e.Source == NodeActivity.Mesh)
            .Sum(e => Math.Max(0, (now - (e.StartedAt > dayStart ? e.StartedAt : dayStart)).TotalSeconds));
        return usedSeconds + running;
    }

    private void LoadUsage()
    {
        usageDay = Today(time.GetUtcNow());

        if (usageFile is null || !File.Exists(usageFile))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<UsageRecord>(File.ReadAllText(usageFile));

            if (saved is not null && DateOnly.TryParse(saved.Day, out var day) && day == usageDay)
            {
                usedSeconds = Math.Max(0, saved.Seconds);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not read today's network usage from {File} ({Message}); counting from zero.", usageFile, ex.Message);
        }
    }

    private void SaveUsageLocked()
    {
        if (usageFile is null || options.CurrentValue.Sharing.DailyNetworkMinutes <= 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(usageFile))!);
            var temp = usageFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new UsageRecord(usageDay.ToString("yyyy-MM-dd"), Math.Round(usedSeconds, 1))));
            File.Move(temp, usageFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not save today's network usage to {File}: {Message}.", usageFile, ex.Message);
        }
    }

    private sealed record UsageRecord(string Day, double Seconds);

    private sealed record HoursCache(SharingOptions Source, SharingHours Hours);
}

/// <summary>Today's network work against <c>Node:Sharing:DailyNetworkMinutes</c> (116). <c>LeftSeconds</c> null: no allowance.</summary>
public sealed record SharingAllowance(int Minutes, double UsedSeconds, double? LeftSeconds, DateTimeOffset ResetsAtUtc);
