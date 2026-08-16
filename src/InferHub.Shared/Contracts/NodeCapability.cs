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
    /// Text to image (phase 46). Declared by a tool runtime, exactly as the two audio kinds are —
    /// the capability seam took a whole new modality with <b>no protocol change</b>, which is the
    /// thing phase 40 was built to make possible.
    /// </summary>
    /// <remarks>
    /// Generating is deliberately not editing. Phase 50 added a second kind,
    /// <see cref="ImageEdit"/>, rather than a per-model operation list, because the router filters
    /// on <c>(kind, model)</c> and nothing else — teaching it to read a nested operation set would
    /// mean teaching the affinity, the queue and the saturation logic the same thing. It is also a
    /// real distinction: FLUX.1-schnell has no official inpainting pipeline and SDXL does.
    /// </remarks>
    public const string Image = "image";

    /// <summary>
    /// Image to image: inpainting with a mask, plain img2img without one, and variations
    /// (phase 50, D1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node declares this for exactly the recipes whose <c>operations</c> include <c>edit</c> or
    /// <c>variation</c>, so a box holding only <c>flux-schnell</c> declares <c>image</c> and an
    /// <b>empty</b> <c>image-edit</c> — which is a statement rather than a silence, and is what
    /// turns "FLUX cannot inpaint" into a <c>503</c> at the edge instead of a stack trace out of a
    /// pipeline forty seconds later.
    /// </para>
    /// <para>
    /// <b>It is a separate kind and not a flag</b> because the routing key has two dimensions and
    /// only two. Everything downstream — affinity, the queue, saturation, the phase-47 job pump —
    /// already speaks <c>(kind, model)</c>, and none of it learned anything in phase 50.
    /// </para>
    /// </remarks>
    public const string ImageEdit = "image-edit";

    /// <summary>
    /// Text to video (phase 57). Declared by the <em>same</em> tool runtime and the same worker the
    /// two image kinds come from — a recipe says <c>"media": "video"</c> and the worker declares this
    /// kind for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seam took a third modality with no protocol change, which is phase-40 D1 paying for itself
    /// for the third time: a <c>ToolJob</c> carries the request, and neither <c>InferenceJob</c>, the
    /// dispatcher, the router nor the mesh learned anything.
    /// </para>
    /// <para>
    /// <b>Image-to-video is deliberately not this kind and is not in this release</b> (57's non-goals).
    /// It takes a second input path and is 50 D1's argument unchanged — a separate kind rather than a
    /// flag, because the router filters on <c>(kind, model)</c> and nothing else.
    /// </para>
    /// </remarks>
    public const string Video = "video";

    /// <summary>
    /// Only used at the client edge, for error messages. The mesh carries any string — see the
    /// remarks on <see cref="NodeCapability"/>.
    /// </summary>
    public static bool IsWellKnown(string? kind) =>
        kind is Chat or Embed or Transcribe or Speak or Image or ImageEdit or Video;

    /// <summary>
    /// Either image kind — the question everything that reasons about a <em>recipe</em> asks.
    /// </summary>
    /// <remarks>
    /// Editing and generating are separate for <b>routing</b>, which is what phase-50 D1 is about.
    /// They are not separate for the licence gate, the VRAM budget or the residency map, because an
    /// edit loads exactly the same weights: <c>AutoPipelineForInpainting.from_pipe</c> reuses the
    /// components rather than loading a second copy. A node that applied its licence gate to
    /// <c>image</c> only would happily edit with a model whose licence nobody accepted.
    /// </remarks>
    public static bool IsImageKind(string? kind) =>
        string.Equals(kind, Image, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, ImageEdit, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Any kind served from a <em>recipe</em> — the two image kinds and video (phase 57, D3).
    /// </summary>
    /// <remarks>
    /// This is <see cref="IsImageKind"/>'s argument one level up, and it is the predicate the licence
    /// gate, the VRAM budget and the residency map ask. A node that applied its licence gate to the
    /// image kinds only would happily render video with weights whose licence nobody accepted — which
    /// is exactly the failure phase 50 headed off between generating and editing, in a release where
    /// there was only one more kind to forget.
    /// </remarks>
    public static bool IsGenerativeMedia(string? kind) =>
        IsImageKind(kind) || IsVideo(kind);

    /// <summary>The video kind, as a predicate the job routes scope on (phase 57).</summary>
    public static bool IsVideo(string? kind) =>
        string.Equals(kind, Video, StringComparison.OrdinalIgnoreCase);

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
