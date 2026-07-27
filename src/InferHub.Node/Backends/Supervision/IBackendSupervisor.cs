namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// What a probe can conclude about a local inference server. Three states, not two, because
/// the cure differs: a server that is not running has to be <em>started</em>, and one that is
/// running but stuck has to be <em>stopped first</em> — <c>start</c> on a wedged process fails
/// with the port already bound, and the log then blames the wrong thing.
/// </summary>
public enum BackendHealth
{
    /// <summary>Answered inside the probe deadline.</summary>
    Healthy,

    /// <summary>Connection refused, DNS failure, socket error — nothing is listening.</summary>
    Unreachable,

    /// <summary>The socket connects (or a 5xx comes back) but no usable answer arrives.</summary>
    Wedged
}

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
