using InferHub.Node.Configuration;
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
    /// What this runtime is doing, for the hub to record (phase 45). It includes the manifests that
    /// were loaded and <em>not</em> started, because "I put the file there and nothing happened" is
    /// the question phase-41 D2 answers in a log line on a box nobody is tailing.
    /// </summary>
    NodeToolState State(string nodeId);

    /// <summary>
    /// Takes an exclusive hold on a worker for this pair, starting one if the pool has room.
    /// Throws <see cref="ToolNotProvidedException"/>, <see cref="ToolBusyException"/> or
    /// <see cref="ToolUnavailableException"/> — each of which the edge renders as its own status.
    /// </summary>
    Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken);

    /// <summary>
    /// Takes a worker for a named <em>tool</em>, whatever it currently declares (phase 48, D4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the one path that does not go through a capability, and it has to be.</b> A pull
    /// exists precisely because the model is <em>not</em> there yet, so it is not declared, so
    /// <see cref="AcquireAsync"/> would answer "this node does not provide it" — correctly, and
    /// uselessly. What is being addressed here is the tool, which <c>Tools:Allowed</c> named and
    /// the operator granted.
    /// </para>
    /// <para>
    /// It stays a ceiling: a tool id that has no pool is refused, so this cannot start a manifest
    /// the operator did not allow any more than a profile could (phase-41 D2). And it takes an
    /// ordinary worker lease, so a pull occupies the same slot a generation would — a node does not
    /// quietly grow a second lane to the GPU.
    /// </para>
    /// </remarks>
    Task<ToolWorkerLease> AcquireToolAsync(string toolId, CancellationToken cancellationToken);

    /// <summary>
    /// The tool ids that have a running pool — what a hub-issued model command may name.
    /// </summary>
    IReadOnlyList<string> ToolIds { get; }

    /// <summary>
    /// The image catalogue this node read off disk (phase 48), for the profile clamp to refuse
    /// against. Empty when there is no image tool.
    /// </summary>
    IReadOnlyList<ImageRecipeInfo> ImageRecipes { get; }

    /// <summary>
    /// Narrows which models this node declares, on top of everything else (phase 48). The set has
    /// been through <c>NodeProfileClamp</c>, so it can only ever remove.
    /// </summary>
    void SetDisabledModels(IReadOnlyCollection<string> models);

    /// <summary>
    /// Stops the named tools and starts every other allowed one (phase 43). The set is what survived
    /// <c>NodeProfileClamp</c>, so it can only ever be a subset of <c>Tools:Allowed</c> — the hub is
    /// narrowing something the operator already granted, never naming a tool this node has not been
    /// told it may run.
    /// </summary>
    /// <remarks>
    /// A stopped pool withdraws its capabilities, which unroutes the node for that work through the
    /// phase-36 D7 mechanism — the same one a broken backend and a given-up pool already use. There
    /// is no third notion of "off".
    /// </remarks>
    Task SetDisabledToolsAsync(IReadOnlyCollection<string> toolIds, CancellationToken cancellationToken);
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

    public Task<ToolWorkerLease> AcquireToolAsync(string toolId, CancellationToken cancellationToken)
        => throw new ToolUnavailableException(
            $"this node has no tool runtime, so it cannot manage models for '{toolId}'. Set {ToolOptions.SectionName}:Enabled=true and name it in {ToolOptions.SectionName}:Allowed.");

    public IReadOnlyList<string> ToolIds => Array.Empty<string>();

    public IReadOnlyList<ImageRecipeInfo> ImageRecipes => Array.Empty<ImageRecipeInfo>();

    /// <summary>Nothing to narrow. A profile naming a recipe here has already been refused.</summary>
    public void SetDisabledModels(IReadOnlyCollection<string> models)
    {
    }

    /// <summary>
    /// <c>enabled: false</c> and an empty list — the honest answer for a box with no runtime, and
    /// distinguishable from a box whose runtime is on and has nothing in it.
    /// </summary>
    public NodeToolState State(string nodeId) => NodeToolState.Off(nodeId);

    /// <summary>
    /// Nothing to switch off. A profile that names tools on a node with the runtime off has already
    /// been refused by the clamp, so this can only ever be called with an empty set.
    /// </summary>
    public Task SetDisabledToolsAsync(IReadOnlyCollection<string> toolIds, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
