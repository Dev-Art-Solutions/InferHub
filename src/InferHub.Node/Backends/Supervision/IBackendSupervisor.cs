using InferHub.Shared.Contracts;

namespace InferHub.Node.Backends.Supervision;

// BackendHealth moved to InferHub.Shared.Contracts in phase 69: the hub routes on it now, so it
// is part of the Heartbeat contract rather than a node-local verdict.

/// <summary>
/// Which half of supervision this node consented to (phase 69, D4). <b>Watching is not
/// restarting.</b> Bouncing a process needs consent and needs to be local — restarting somebody
/// else's inference server is not this node's business, which is why
/// <c>Ollama:Supervisor:Enabled</c> is off by default and loopback-only. <em>Asking</em> a server
/// whether it is alive needs neither: it is what the next request does anyway.
/// </summary>
/// <param name="MayRestart">
/// False when the node probes and reports but never touches the process. The supervisor still
/// declares health on the same threshold, so the hub still stops routing here — the difference is
/// only whether anybody tries to fix it locally.
/// </param>
public sealed record BackendSupervisionMode(bool MayRestart);

/// <summary>
/// The read side of backend supervision, deliberately named for the <em>node</em> rather than
/// for Ollama: it is the one thing outside <c>Backends/Supervision/</c> that anybody consumes
/// (see <see cref="CoordinatorConnection"/>), and design rule 1 says nothing on the node's
/// generic path may learn which backend it is talking to.
/// </summary>
/// <remarks>
/// <see cref="NoBackendSupervisor"/> is registered whenever supervision is off, so no consumer
/// has to branch on whether the feature exists.
/// </remarks>
public interface IBackendSupervisor
{
    /// <summary>False when nothing is being supervised, in which case <see cref="Health"/> is null.</summary>
    bool IsSupervising { get; }

    /// <summary>
    /// The last <em>declared</em> state — null until the supervisor has made up its mind. A
    /// single failed probe never moves it; only the consecutive-failure threshold does.
    /// </summary>
    BackendHealth? Health { get; }

    /// <summary>Raised once per outage, the moment the backend answers again.</summary>
    event Action? Recovered;

    /// <summary>
    /// Raised immediately before a restart, which aborts whatever is in flight. The subscriber
    /// that owns the in-flight count logs what the restart is about to cost.
    /// </summary>
    event Action<BackendHealth>? Restarting;
}

/// <summary>The registered implementation when supervision is off or does not apply.</summary>
public sealed class NoBackendSupervisor : IBackendSupervisor
{
    public bool IsSupervising => false;

    public BackendHealth? Health => null;

    public event Action? Recovered
    {
        add { }
        remove { }
    }

    public event Action<BackendHealth>? Restarting
    {
        add { }
        remove { }
    }
}
