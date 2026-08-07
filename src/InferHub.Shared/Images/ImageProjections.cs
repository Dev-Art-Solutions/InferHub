namespace InferHub.Shared.Images;

/// <summary>
/// What a generated image <em>is</em>, geometrically — a declared property of a result, carried to
/// the client on every surface (phase 49, D4).
/// </summary>
/// <remarks>
/// <para>
/// A 2048×1024 equirectangular panorama and a 2048×1024 landscape photograph are the same bytes in
/// the same shape and are two completely different pictures. Today every client guesses from the
/// aspect ratio, which is wrong for any 2:1 landscape photo and wrong in the direction that produces
/// a warped, unopenable mess in a headset. So the worker states it, and it survives to the response
/// body, to the job document and to the content route's headers.
/// </para>
/// <para>
/// <b>A flat recipe reports <see cref="Flat"/> rather than omitting the field</b>, and that is a
/// deliberate exception to phase-28 D5's "absence is a fact". There, absence meant "nothing has been
/// measured"; here the field is a <em>declaration</em>, and an omitted one would be
/// indistinguishable from a node too old to have an opinion. A client that has to tell those apart
/// has learnt nothing.
/// </para>
/// </remarks>
public static class ImageProjections
{
    /// <summary>An ordinary picture. What every recipe before <c>qwen-360</c> produces.</summary>
    public const string Flat = "flat";

    /// <summary>
    /// A 360°×180° panorama in equirectangular projection: longitude across, latitude down, left
    /// edge continuing into the right edge. Always 2:1 — see <see cref="IsPanoramic"/>.
    /// </summary>
    public const string Equirectangular = "equirectangular";

    /// <summary>
    /// On <c>GET /api/images/jobs/{id}/content/{index}</c>, beside the media type.
    /// </summary>
    /// <remarks>
    /// The bytes are a PNG either way, so a client fetching one image has nowhere else to learn this
    /// — the JSON that carried it is a different request, and one it may not have made.
    /// </remarks>
    public const string Header = "X-InferHub-Image-Projection";

    /// <summary>
    /// What the worker said, or <see cref="Flat"/>. An unrecognised value is <b>kept</b>, not
    /// flattened: a future worker's projection is the worker's to name, and rewriting it to "flat"
    /// would be this library asserting something false about bytes it has never looked at.
    /// </summary>
    public static string Normalise(string? declared) =>
        string.IsNullOrWhiteSpace(declared) ? Flat : declared.Trim().ToLowerInvariant();

    public static bool IsEquirectangular(string? declared) =>
        string.Equals(Normalise(declared), Equirectangular, StringComparison.Ordinal);

    /// <summary>
    /// Whether a size is the 2:1 an equirectangular render has to be.
    /// </summary>
    /// <remarks>
    /// It is not a tolerance and it is not rounded. 360° of longitude over 180° of latitude is
    /// exactly two to one, and a render at any other ratio does not fail — it produces a panorama
    /// that wraps wrongly, which nobody sees until somebody puts on a headset three days later.
    /// </remarks>
    public static bool IsPanoramic(ImageSize size) => size.Width == size.Height * 2;
}
