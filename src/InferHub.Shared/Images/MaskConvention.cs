namespace InferHub.Shared.Images;

/// <summary>
/// Which way round a mask reads — <b>the single most misunderstood thing in this whole track</b>
/// (phase 50, D2).
/// </summary>
/// <remarks>
/// <para>
/// OpenAI's edits API treats a <b>fully transparent</b> pixel as the area to edit. <c>diffusers</c>'
/// inpainting pipelines take a mask where <b>white</b> is the area to inpaint. <b>These are
/// opposite</b>, and getting it backwards does not fail: it edits everything <em>except</em> what
/// the caller selected, which reads as a broken model rather than a backwards mask. Roughly
/// everybody gets it wrong once, which is why it is written down here, in the README, and with a
/// picture on the docs site.
/// </para>
/// <para>
/// <b>The conversion itself is the worker's, and that is a recorded deviation from the phase
/// brief.</b> The brief put a <c>MaskConverter</c> in this library. It cannot live here: turning one
/// convention into the other means reading an alpha channel out of a PNG and writing a greyscale one
/// back, and <b>nothing in InferHub's C# ever decodes a pixel</b> (phase-46 D6) — there is no image
/// library on the hub, by design and by invariant, and hand-rolling a PNG decoder to avoid taking
/// one would be the same mistake with more code. So what is decided <em>here</em> is what the two
/// conventions <b>are</b>, and what a caller may say; the inversion happens where PIL already is.
/// </para>
/// <para>
/// The consequence is the same one phase-46 D6 and phase-49 D3 already accepted twice: a mask with
/// no alpha channel costs one round trip to find out, because the edge cannot open it. The worker
/// answers <c>invalid_request</c> and <see cref="ImageRenderer"/> renders the <c>400</c> without
/// reading the message (phase-29 D6).
/// </para>
/// </remarks>
public static class MaskConventions
{
    /// <summary>
    /// OpenAI's, and the default: <b>transparent pixels are the area to edit</b>.
    /// </summary>
    /// <remarks>
    /// The default because it is the one every OpenAI SDK's users already have masks for, and
    /// because defaulting to the other one would silently invert every mask written against the
    /// documented API this endpoint claims to speak.
    /// </remarks>
    public const string OpenAi = "openai";

    /// <summary>
    /// The library's, for a caller who already has one: <b>white pixels are the area to edit</b>.
    /// </summary>
    /// <remarks>
    /// It exists so somebody with a white-is-edit mask can say so <em>explicitly</em> rather than
    /// inverting their own file to satisfy a convention they did not choose. Saying so is the point:
    /// a mask that arrives with no alpha channel and no declaration is refused, never guessed at.
    /// </remarks>
    public const string Luminance = "luminance";

    public static bool IsKnown(string? value) =>
        Normalise(value) is OpenAi or Luminance;

    public static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? OpenAi : value.Trim().ToLowerInvariant();

    /// <summary>
    /// The refusal, naming both conventions <em>and what each one means</em>.
    /// </summary>
    /// <remarks>
    /// Listing the two words without saying which is which would be a refusal that makes somebody
    /// guess between two options whose difference is invisible until they look at the picture.
    /// </remarks>
    public static string Refusal(string? asked) =>
        $"{ImageExtensions.MaskConvention}: '{asked}' is not a mask convention. Use " +
        $"'{OpenAi}' (the default — TRANSPARENT pixels are the area to edit, as OpenAI's API " +
        $"defines it) or '{Luminance}' (WHITE pixels are the area to edit, as diffusers defines it).";
}
