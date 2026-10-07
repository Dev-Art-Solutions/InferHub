using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node with a colibri catalogue says about it (phase 97): every converted model it may load,
/// which ones are loaded, which the hub pinned, and whether idle ones are stopped.
/// </summary>
/// <remarks>
/// The phase-44 D6 mailbox again: the node reports on its own loop and whenever a model loads or
/// stops, the hub records it, and <b>the hub never asks</b>. A node without a catalogue (one
/// <c>Colibri:Serve:Model</c>, or no colibri at all) sends none.
/// </remarks>
public sealed record NodeColibriState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    /// <summary>Whether an idle, unpinned model is stopped to free its RAM — the effective value, after a profile.</summary>
    [property: JsonPropertyName("onDemand")] bool OnDemand,
    [property: JsonPropertyName("idleUnloadSeconds")] double IdleUnloadSeconds,
    /// <summary>How many models may be loaded at once (<c>Colibri:Serve:MaxLoaded</c>) — the operator's RAM statement.</summary>
    [property: JsonPropertyName("maxLoaded")] int MaxLoaded,
    /// <summary>Whether the catalogue serves at all — false for a colibri engine the hub stopped.</summary>
    [property: JsonPropertyName("running")] bool Running,
    [property: JsonPropertyName("models")] IReadOnlyList<NodeColibriModel> Models,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc);

/// <summary>One converted model in a node's catalogue.</summary>
public sealed record NodeColibriModel(
    [property: JsonPropertyName("name")] string Name,
    /// <summary><see cref="Loaded"/> | <see cref="Loading"/> | <see cref="Unloaded"/> | <see cref="Failed"/>.</summary>
    [property: JsonPropertyName("state")] string State,
    /// <summary>The hub's profile asks for it to stay loaded; it is never evicted or idled out.</summary>
    [property: JsonPropertyName("pinned")] bool Pinned,
    [property: JsonPropertyName("inFlight")] int InFlight,
    /// <summary>Seconds since its last request finished, while loaded. Null when not loaded.</summary>
    [property: JsonPropertyName("idleSeconds")] double? IdleSeconds = null,
    [property: JsonPropertyName("lastError")] string? LastError = null)
{
    public const string Loaded = "loaded";

    /// <summary><c>coli serve</c> is running and has not answered yet — reading the dense weights.</summary>
    public const string Loading = "loading";

    public const string Unloaded = "unloaded";

    /// <summary>Its last load did not come up; <see cref="LastError"/> says why. The next request tries again.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// The colibri block of a profile (phase 97): which catalogue models stay loaded, and whether idle
/// ones are stopped.
/// </summary>
/// <remarks>
/// <b>Names, never paths.</b> The catalogue is the operator's directory on the box, and a profile
/// picks from it the way <c>backends</c> picks from <c>Backend:Engines</c> (43 D1): a name the box
/// does not have is refused naming the ones it has, and more names than <c>Serve:MaxLoaded</c> is
/// refused past the count, because that number is a statement about the box's RAM.
/// </remarks>
public sealed record ColibriProfile(
    /// <summary>Models to keep loaded. Null leaves the box's <c>Serve:Preload</c>; empty pins nothing.</summary>
    [property: JsonPropertyName("loaded")] IReadOnlyList<string>? Loaded = null,
    /// <summary>Null leaves the box's <c>Serve:OnDemand</c>.</summary>
    [property: JsonPropertyName("onDemand")] bool? OnDemand = null);
