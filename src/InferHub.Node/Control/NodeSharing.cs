using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Control;

/// <summary>
/// Whether this node takes new network jobs now (phase 113) — from its owner's <c>Node:Sharing:Mode</c>, read live,
/// and, in <c>idle</c> mode, from what <see cref="NodeActivity"/> says this machine itself is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node decides; the hub is told</b> (113 D1). <see cref="Changed"/> is what the mesh connection reports
/// through the mailbox. Pausing never cancels a running network job (D3): only new ones wait.
/// </para>
/// <para>
/// <b>idle</b> pauses before the first local job runs — <see cref="NodeActivity.Changed"/> is raised from
/// <see cref="NodeActivity.Begin"/>, ahead of the work — and resumes <c>IdleSeconds</c> after the last one ends.
/// </para>
/// </remarks>
public sealed class NodeSharing : IHostedService, IDisposable
{
    private readonly IOptionsMonitor<NodeOptions> options;
    private readonly NodeActivity activity;
    private readonly TimeProvider time;
    private readonly ILogger<NodeSharing> logger;
    private readonly object gate = new();
    private IDisposable? optionsSubscription;
    private ITimer? resumeTimer;
    private DateTimeOffset? lastLocalEnd;
    private NodeSharingState state;

    public NodeSharing(IOptionsMonitor<NodeOptions> options, NodeActivity activity, ILogger<NodeSharing> logger, TimeProvider? time = null)
    {
        this.options = options;
        this.activity = activity;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        state = new NodeSharingState(string.Empty, Mode, false, null, this.time.GetUtcNow(), 0, 0);
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
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
        if (entry.Source != NodeActivity.Local)
        {
            return;
        }

        if (entry.EndedAt is not null)
        {
            lock (gate)
            {
                lastLocalEnd = entry.EndedAt;
            }
        }

        Recompute();
    }

    internal void Recompute()
    {
        var mode = Mode;
        var local = activity.RunningCount(NodeActivity.Local);
        var now = time.GetUtcNow();
        var idleFor = TimeSpan.FromSeconds(Math.Max(0, options.CurrentValue.Sharing.IdleSeconds));
        bool paused;
        string? reason;
        TimeSpan? resumeIn = null;

        lock (gate)
        {
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
                    resumeIn = idleFor - (now - end);
                    reason = $"its owner used it {Math.Round((now - end).TotalSeconds)} s ago; sharing resumes after {idleFor.TotalSeconds:0} s idle";
                    break;

                default:
                    paused = false;
                    reason = null;
                    break;
            }

            if (state.Paused == paused && state.Mode == mode && state.Reason == reason)
            {
                ScheduleResume(resumeIn);
                return;
            }

            var since = state.Paused == paused && state.Mode == mode ? state.SinceUtc : now;
            state = state with { Mode = mode, Paused = paused, Reason = reason, SinceUtc = since };
            ScheduleResume(resumeIn);
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

    private void ScheduleResume(TimeSpan? after)
    {
        resumeTimer?.Dispose();
        resumeTimer = after is { } delay && delay > TimeSpan.Zero
            ? time.CreateTimer(_ => Recompute(), null, delay + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan)
            : null;
    }
}
