namespace InferHub.Shared.Contracts;

/// <summary>
/// The units fleet work is measured in, as they appear in a usage row (phase 42, D7).
/// </summary>
/// <remarks>
/// <para>
/// They live in <c>InferHub.Shared</c> rather than beside the ledger because the node's audio edge
/// decides which unit a request is in, and the coordinator's ledger writes it — two projects, one
/// spelling. A solo node and a hub that disagreed about whether the string is
/// <c>audio_seconds</c> or <c>audioSeconds</c> would produce two ledgers nobody can add together.
/// </para>
/// <para>
/// Rule 7 is unchanged by their existence: these name a <em>count</em>, and there is deliberately no
/// unit whose value is text.
/// </para>
/// </remarks>
public static class UsageUnitKinds
{
    public const string Tokens = "tokens";

    public const string AudioSeconds = "audio_seconds";

    public const string Characters = "characters";

    /// <summary>
    /// Image generation (phase 46): <c>width × height × steps / 1e6</c>, summed over the images a
    /// request produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not "images".</b> A 512×512 image at 4 steps and a 2048×1024 one at 30 steps are both
    /// "one image", and the second is <b>47 times</b> the work. A counter that bills them the same
    /// is not a rounding error, it is a number whose wrongness scales with how much somebody uses
    /// the expensive path — and the person it under-charges is the person costing the most GPU.
    /// </para>
    /// <para>
    /// Pixels × steps is what a diffusion transformer actually spends: every step is one pass over
    /// the whole latent. It is not exact across models (a 20B transformer costs more per
    /// megapixel-step than a 0.9B UNet) and it is not meant to be — <c>UsageRecord</c> carries the
    /// model, so a rate card that cares can price per model. What this unit has to be is
    /// <em>proportional within a model</em>, and it is.
    /// </para>
    /// </remarks>
    public const string MegapixelSteps = "megapixel_steps";

    /// <summary>
    /// Video generation (phase 57): the duration of what was produced, <c>frames / fps</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a second unit on the same work, not a replacement for
    /// <see cref="MegapixelSteps"/></b> — phase 42's audio precedent exactly, where a transcription
    /// meters seconds and a synthesis meters characters. The GPU cost of a video is still
    /// <c>width × height × frames × steps / 1e6</c> megapixel-steps, because a video diffusion
    /// transformer denoises the <em>whole</em> latent stack on every step, so pixels × steps is
    /// literally what the card spent and the existing quota governs the same card. What this unit
    /// adds is the question a human actually asks — "how much video did this client make" — which
    /// megapixel-steps cannot answer at all.
    /// </para>
    /// <para>
    /// <b>Considered and rejected: a unit of its own for the quota</b> (the track index guessed
    /// <c>frame_seconds</c>). It would be a second answer to a question already answered, and it
    /// would let a client whose image budget is exhausted spend the same GPU under a counter nobody
    /// had set.
    /// </para>
    /// </remarks>
    public const string VideoSeconds = "video_seconds";
}
