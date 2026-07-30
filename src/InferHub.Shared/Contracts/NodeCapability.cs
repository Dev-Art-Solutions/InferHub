using System.Text.Json.Serialization;

namespace InferHub.Shared.Contracts;

/// <summary>
/// What a node can <em>do</em> with a set of models, as opposed to which models it holds
/// (phase 40). The unit of routing is the pair <c>(kind, model)</c>: a flat model list is a
/// routing key with one dimension, and it encodes an assumption — that every model on a node
/// does the same kind of work — which stops being true the moment a node holds an embedding
/// model, a speech model, or anything a tool runtime provides.
/// </summary>
/// <remarks>
/// <see cref="Kind"/> is a plain string on the wire, not an enum. A node running a capability an
/// older coordinator has never heard of registers normally and is simply never routed for it,
/// which is what a fleet upgraded one box at a time needs.
/// </remarks>
public sealed record NodeCapability(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("models")] IReadOnlyList<string> Models);

/// <summary>The kinds this release knows how to route. Others are carried, not understood.</summary>
public static class CapabilityKinds
{
    public const string Chat = "chat";

    public const string Embed = "embed";

    /// <summary>Speech to text. Declared by a tool runtime from phase 41; nothing serves it yet.</summary>
    public const string Transcribe = "transcribe";

    /// <summary>Text to speech. Declared by a tool runtime from phase 41; nothing serves it yet.</summary>
    public const string Speak = "speak";

    /// <summary>
    /// Only used at the client edge, for error messages. The mesh carries any string — see the
    /// remarks on <see cref="NodeCapability"/>.
    /// </summary>
    public static bool IsWellKnown(string? kind) =>
        kind is Chat or Embed or Transcribe or Speak;

    /// <summary>
    /// The capability an Ollama-shaped job kind needs. <c>generate</c> and <c>chat</c> are both
    /// text generation and share one capability; a node that can do one can do the other, because
    /// the backend endpoint behind them is the same. Anything unrecognised (today: the internal
    /// <c>vector-query</c> job) routes without a capability filter, exactly as it did before.
    /// </summary>
    public static string? ForJobKind(string? jobKind) => jobKind switch
    {
        "chat" or "generate" => Chat,
        "embed" => Embed,
        _ => null
    };
}
