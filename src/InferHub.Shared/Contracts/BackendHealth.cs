namespace InferHub.Shared.Contracts;

/// <summary>
/// What a probe can conclude about a node's local inference server. Three states, not two, because
/// the cure differs: a server that is not running has to be <em>started</em>, and one that is
/// running but stuck has to be <em>stopped first</em> — <c>start</c> on a wedged process fails
/// with the port already bound, and the log then blames the wrong thing.
/// </summary>
/// <remarks>
/// Moved here from <c>InferHub.Node.Backends.Supervision</c> in phase 69, when it stopped being a
/// node-local verdict and became part of <see cref="Heartbeat"/> — the hub routes on it. It is a
/// plain enum, so rule 2 is untouched.
/// </remarks>
public enum BackendHealth
{
    /// <summary>Answered inside the probe deadline.</summary>
    Healthy,

    /// <summary>Connection refused, DNS failure, socket error — nothing is listening.</summary>
    Unreachable,

    /// <summary>The socket connects (or a 5xx comes back) but no usable answer arrives.</summary>
    Wedged
}
