using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node says about the corpus it is hosting (phase 44, D6): whether it is running, which
/// engine, which collections it owns and how many records are in them, and the last thing that went
/// wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hub reports what the node told it, and never queries a node's corpus to build a status
/// page.</b> Asking would make <c>/api/status</c> a synchronous dependency on a box that may be
/// asleep, mid-reboot or behind a router that dropped the connection three seconds ago — and
/// <c>/api/status</c> has to answer when the fleet does not. Phase-26 D1 for a third time: the hub
/// never dials a node.
/// </para>
/// <para>
/// So a stale block here is the honest failure mode, and it is a cheap one: the <see cref="AtUtc"/>
/// stamp says when the node last spoke, and a node that has gone away stops being listed at all.
/// </para>
/// </remarks>
public sealed record NodeCorpusState(
    [property: JsonPropertyName("nodeId")] string NodeId,
    /// <summary>Whether a corpus is meant to be running here at all.</summary>
    [property: JsonPropertyName("enabled")] bool Enabled,
    /// <summary><c>local</c> or <c>qdrant</c>.</summary>
    [property: JsonPropertyName("provider")] string Provider,
    /// <summary><see cref="Running"/>, <see cref="Stopped"/> or <see cref="Failed"/>.</summary>
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("collections")] IReadOnlyList<NodeCorpusCollection> Collections,
    /// <summary>
    /// Why the corpus is not running, when it is not. A start that failed leaves the node with no
    /// corpus at all rather than a half-started one (D3), so this is the whole of the explanation.
    /// </summary>
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("atUtc")] DateTimeOffset AtUtc)
{
    public const string Running = "running";

    public const string Stopped = "stopped";

    public const string Failed = "failed";

    public static NodeCorpusState Off(string nodeId) => new(
        nodeId,
        Enabled: false,
        Provider: "local",
        Status: Stopped,
        Collections: Array.Empty<NodeCorpusCollection>(),
        Error: null,
        DateTimeOffset.UtcNow);
}

/// <summary>One collection this node owns, as the node counts it.</summary>
public sealed record NodeCorpusCollection(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dimension")] int Dimension,
    [property: JsonPropertyName("records")] long Records);
