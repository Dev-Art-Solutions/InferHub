using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// Who holds an on-demand node's card right now (phase 86), carried on <see cref="Heartbeat"/> so the
/// hub can send work to a node that needs no switch for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The record's absence and a null <see cref="Holder"/> are different facts.</b> No state at all is
/// "this node does not run on demand" (or is older than v3.51) — it routes exactly as before. A state
/// with a null holder is "on demand, and the card is free right now", which is worth preferring over a
/// node that would have to evict somebody first.
/// </para>
/// <para>
/// <see cref="WarmFor"/> is resolved <b>on the node</b> (86 D2): only the node knows that
/// <c>tool:diffusion</c> means <c>image</c> and <c>video</c>, and the hub never learns what a tenant is.
/// </para>
/// </remarks>
public sealed record OnDemandState(
    /// The tenant holding the card (<c>ollama</c>, <c>tool:&lt;id&gt;</c>), or null when it is free.
    [property: JsonPropertyName("holder")] string? Holder,
    /// The capability kinds the holder serves, so a request of one of these needs no switch.
    [property: JsonPropertyName("warmFor")] IReadOnlyList<string> WarmFor,
    /// A release is running: the card is between tenants and nobody is served until it finishes.
    [property: JsonPropertyName("switching")] bool Switching,
    /// Requests queued for a tenant that does not hold the card.
    [property: JsonPropertyName("waiting")] int Waiting);
