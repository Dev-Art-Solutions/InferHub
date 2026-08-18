namespace InferHub.Coordinator.Auth;

/// <summary>
/// A named inference client (phase 25): an id, a key, and an optional set of limits. Lives in
/// the <c>Auth:Clients</c> list. The legacy flat <c>Auth:ApiKeys</c> list keeps working — its
/// entries become anonymous clients with no limits, so nobody's config breaks.
/// </summary>
public sealed class ClientConfig
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Environment or user-secrets only. Never <c>appsettings.json</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public ClientLimits? Limits { get; set; }

    /// <summary>
    /// RAG collections this client may touch (phase 31). <c>null</c>/absent = all collections,
    /// which is what every key had before v2.13 — so a config that never heard of scoping is
    /// unchanged. Entries are exact names or a single trailing-<c>*</c> prefix (<c>tenant-a-*</c>).
    /// Unlike <see cref="ClientLimits"/> this is not a limit but an isolation boundary, so it sits
    /// on the client itself rather than inside a "limits" bag an operator might read as advisory.
    /// </summary>
    public List<string>? Collections { get; set; }
}

/// <summary>All limits are nullable; <c>null</c> means unlimited.</summary>
public sealed class ClientLimits
{
    public int? MaxConcurrent { get; set; }

    public int? RequestsPerMinute { get; set; }

    public long? TokensPerMinute { get; set; }

    public long? TokensPerDay { get; set; }

    /// <summary>
    /// Seconds of audio this client may have transcribed per UTC day (phase 42, D7). Audio has no
    /// token count, so a token budget cannot bound it — a client with <c>TokensPerDay</c> set and
    /// nothing else could transcribe a library and never touch its budget.
    /// </summary>
    public double? AudioSecondsPerDay { get; set; }

    /// <summary>Characters this client may have synthesised per UTC day. The unit TTS bills in.</summary>
    public double? CharactersPerDay { get; set; }

    /// <summary>
    /// Megapixel-steps this client may generate per UTC day (phase 46, D-note on the unit). Image
    /// generation consumes no tokens, no seconds and no characters, so none of the three existing
    /// budgets can bound it — a client with <c>TokensPerDay</c> set and nothing else could keep a
    /// card busy indefinitely and never touch a limit.
    /// </summary>
    /// <remarks>
    /// A megapixel-step is <c>width × height × steps / 1e6</c>. One 1024×1024 image at 30 steps is
    /// ≈31.5; a working day of casual use is a few thousand. It is deliberately not "images per
    /// day", because that number bills a 4-step thumbnail and a 30-step 2-megapixel render the same.
    /// </remarks>
    public double? MegapixelStepsPerDay { get; set; }

    /// <summary>
    /// Seconds of video this client may have generated per UTC day (phase 59, D3). The knob phase 57
    /// deferred until there was a catalogue to spend it on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Video is billed in <b>both</b> units (57 D6): megapixel-steps, because it is the same card an
    /// image spends, and seconds, because that is the question a human asks. Until this release only
    /// the first was a gate — which is 42 D7's rule failing in a new unit, since a client whose only
    /// limit is a picture budget renders clips against a figure nobody sized for them. A five-second
    /// clip is ≈970 megapixel-steps against an SDXL image's ≈31.
    /// </para>
    /// <para>
    /// There is deliberately no per-minute companion: a clip's seconds arrive in one lump when the
    /// job ends, minutes after it was admitted, so a sliding window would refuse the wrong request.
    /// The burst control for a four-minute job is <see cref="MaxConcurrent"/>.
    /// </para>
    /// </remarks>
    public double? VideoSecondsPerDay { get; set; }

    /// <summary>
    /// Models this client may use. Empty/null = all. A request outside the list is a 404
    /// identical to a model that does not exist — a client is not told what exists but is
    /// not for them.
    /// </summary>
    public List<string>? AllowedModels { get; set; }

    public bool HasAny =>
        MaxConcurrent is not null
        || RequestsPerMinute is not null
        || TokensPerMinute is not null
        || TokensPerDay is not null
        || AudioSecondsPerDay is not null
        || CharactersPerDay is not null
        || MegapixelStepsPerDay is not null
        || VideoSecondsPerDay is not null
        || AllowedModels is { Count: > 0 };
}
