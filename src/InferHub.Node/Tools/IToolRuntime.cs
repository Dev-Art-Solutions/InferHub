using InferHub.Shared.Contracts;

namespace InferHub.Node.Tools;

/// <summary>
/// The node's second kind of engine (phase 41). Always registered — as
/// <see cref="NoToolRuntime"/> when tools are off — so <b>no caller anywhere branches on the
/// feature existing</b>, which is phase-36 D8's <c>IBackendSupervisor</c> shape.
/// </summary>
/// <remarks>
/// Design rule 1 holds: nothing on the node's inference path (<c>Worker</c>,
/// <c>InferenceExecutor</c>, <c>IInferenceBackend</c>) learns that tools exist. A backend serves
/// chat and embeddings; a tool runtime serves everything else; the two never meet.
/// </remarks>
public interface IToolRuntime
{
    /// <summary>True when a real runtime is loaded. Used for status and logging, never for control flow.</summary>
    bool Enabled { get; }

    /// <summary>
    /// What the loaded tools currently offer, folded into the node's declaration by
    /// <c>BackendCapabilities</c>. It is <em>live</em>: a pool that gives up empties its own list
    /// and the node re-registers without it.
    /// </summary>
    IReadOnlyList<NodeCapability> Capabilities { get; }

    /// <summary>Raised when <see cref="Capabilities"/> changes, so the node re-reports at once.</summary>
    event Action? CapabilitiesChanged;

    /// <summary>
    /// Takes an exclusive hold on a worker for this pair, starting one if the pool has room.
    /// Throws <see cref="ToolNotProvidedException"/>, <see cref="ToolBusyException"/> or
    /// <see cref="ToolUnavailableException"/> — each of which the edge renders as its own status.
    /// </summary>
    Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken);
}

/// <summary>
/// The stand-in registered when <c>Tools:Enabled</c> is false — which is the default, and therefore
/// the shape almost every deployment runs.
/// </summary>
/// <remarks>
/// It exists so that "tools are off" is a fact about one object rather than a null check in five
/// call sites, and so that a v3.8 node upgraded to v3.9 with no config change spawns nothing,
/// declares nothing, and reads a manifest directory it never opens.
/// </remarks>
public sealed class NoToolRuntime : IToolRuntime
{
    public bool Enabled => false;

    public IReadOnlyList<NodeCapability> Capabilities => Array.Empty<NodeCapability>();

    public event Action? CapabilitiesChanged
    {
        add { }
        remove { }
    }

    public Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken)
        => throw new ToolNotProvidedException(capability, model);
}
