namespace InferHub.Shared.Images;

/// <summary>
/// How a 360° render's join may be closed — <b>asked for per request, never applied by default</b>
/// (phase 55, D1).
/// </summary>
/// <remarks>
/// <para>
/// Phase 49 measured the seam and refused to repair it, and every clause of that refusal was about
/// <em>consent</em>: a roll-and-inpaint fix is a second generation pass the caller did not ask for,
/// did not watch, and would be billed for. This is the asking. What survives phase-49 D5 intact is
/// that <b>no threshold ever triggers a repair</b> — `Tools:Image:SeamWarnThreshold` decides whether
/// to <em>warn</em>, and a number that decides to spend somebody's GPU is the tool overriding the
/// person with a helpful expression on.
/// </para>
/// <para>
/// Two vocabularies, deliberately not one. A <b>caller</b> names a mechanism
/// (<see cref="Blend"/> or <see cref="Diffuse"/>, or <see cref="Off"/> to say so explicitly); an
/// <b>operator</b> sets a ceiling in <c>Tools:Image:SeamRepair</c>, which is those three plus
/// <see cref="Any"/>. The ceiling is <see cref="Off"/> by default, so a deployment that changes no
/// config cannot be made to spend a step by a header alone — phase-41 D2's shape, one level down.
/// </para>
/// <para>
/// <b>The ceiling matches exactly, and <see cref="Diffuse"/> does not imply <see cref="Blend"/>.</b>
/// These name mechanisms, not tiers: an operator who thinks a feathered band is worse than an honest
/// seam, and wants only the real repair, has to be able to say that. <see cref="Any"/> is how "both"
/// is said, which is also why it exists rather than being a synonym for the most permissive one.
/// </para>
/// <para>
/// <b>Nothing here decides anything about a picture.</b> The mechanisms run in the worker, where PIL
/// and the pipeline already are, and the numbers arrive on its result frame — phase-46 D6 is
/// unchanged and there is still no image library anywhere in this codebase's C#.
/// </para>
/// </remarks>
public static class SeamRepairModes
{
    /// <summary>No repair. The default, and the whole of v3.22's behaviour.</summary>
    public const string Off = "off";

    /// <summary>
    /// A wrapped feather across a narrow band at the join: numpy on the array the VAE already
    /// produced, <b>milliseconds, no VRAM, no steps, nothing added to the ledger</b>.
    /// </summary>
    /// <remarks>
    /// The trade is stated rather than hidden: it closes a <em>tonal</em> discontinuity and not a
    /// <em>structural</em> one. A seam cutting through a doorway comes back with no visible step in
    /// brightness and the doorway still not lining up — that is what <see cref="Diffuse"/> is for.
    /// </remarks>
    public const string Blend = "blend";

    /// <summary>
    /// An inpainting pass over the join, rolled into the middle of the picture first: the expensive,
    /// better one. Metered as the steps it actually runs, in <c>megapixel_steps</c> (D5).
    /// </summary>
    public const string Diffuse = "diffuse";

    /// <summary>Operator-only: both mechanisms are permitted, and the caller chooses.</summary>
    public const string Any = "any";

    /// <summary>
    /// On <c>GET /api/images/jobs/{id}/content/{index}</c>, beside the projection header — and
    /// <b>only when a repair was asked for</b>.
    /// </summary>
    /// <remarks>
    /// The two numbers ride with it (<see cref="DeltaHeader"/>, <see cref="DeltaBeforeHeader"/>),
    /// because the content route is the one request with no JSON to carry them. They are gated on
    /// the repair rather than emitted for every panorama so that a request which sends no
    /// <see cref="ImageExtensions.SeamRepair"/> header gets a response identical to v3.22 down to
    /// the header list — a claim that is only worth making if it covers everything a client can see.
    /// </remarks>
    public const string Header = "X-InferHub-Image-Seam-Repair";

    public const string DeltaHeader = "X-InferHub-Image-Seam-Delta";

    public const string DeltaBeforeHeader = "X-InferHub-Image-Seam-Delta-Before";

    /// <summary>
    /// The seam headers for one delivered image, or nothing at all. Written here so the hub's
    /// content route and solo mode's cannot disagree about what a fetched panorama says about itself.
    /// </summary>
    /// <remarks>
    /// Invariant formatting, for the reason every number in this project is formatted invariantly: a
    /// decimal comma is a bug that only appears on a Bulgarian or German host, and a header is
    /// parsed by somebody else's client.
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, string>> HeadersFor(ImageJobImage image)
    {
        if (image.SeamRepair is not { } mechanism)
        {
            yield break;
        }

        yield return new KeyValuePair<string, string>(Header, mechanism);

        if (image.SeamDelta is { } delta)
        {
            yield return new KeyValuePair<string, string>(
                DeltaHeader,
                delta.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (image.SeamDeltaBefore is { } before)
        {
            yield return new KeyValuePair<string, string>(
                DeltaBeforeHeader,
                before.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>What a caller may put on the header.</summary>
    public static bool IsMechanism(string? value) =>
        Normalise(value) is Blend or Diffuse;

    /// <summary>What an operator may put in <c>Tools:Image:SeamRepair</c>.</summary>
    public static bool IsCeiling(string? value) =>
        Normalise(value) is Off or Blend or Diffuse or Any;

    public static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Off : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Whether a ceiling permits a mechanism. Exact match, or <see cref="Any"/>.
    /// </summary>
    /// <remarks>
    /// The node holds the answer that counts — this is the same predicate stated in one place so the
    /// key's meaning cannot differ between the validator that checks it and the worker that honours
    /// it. See the remarks above for why <see cref="Diffuse"/> is not a superset of
    /// <see cref="Blend"/>.
    /// </remarks>
    public static bool Permits(string? ceiling, string? asked)
    {
        var permitted = Normalise(ceiling);
        var mechanism = Normalise(asked);

        if (mechanism == Off)
        {
            return true;
        }

        return permitted == Any || permitted == mechanism;
    }

    /// <summary>The refusal for a header value that is not a mechanism, naming both that are.</summary>
    public static string Refusal(string? asked) =>
        $"{ImageExtensions.SeamRepair}: '{asked}' is not a seam-repair mechanism. Use " +
        $"'{Blend}' (a wrapped feather across the join — milliseconds, no steps), " +
        $"'{Diffuse}' (an inpainting pass over the join — slower, billed as the steps it runs), " +
        $"or '{Off}'.";
}
