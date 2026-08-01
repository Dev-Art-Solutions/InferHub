using InferHub.Shared.Contracts;

namespace InferHub.Node.Configuration;

/// <summary>
/// The node's tool runtime (phase 41): child processes the node spawns, supervises, talks to over
/// a line protocol, and restarts when they die.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt in twice, and the second key is a list rather than a boolean</b> (D2).
/// <see cref="Enabled"/> consents to the feature existing; <see cref="Allowed"/> names the
/// manifests that may actually start. A manifest on disk that is not in the list is loaded, logged
/// and never run — nothing is discovered-and-executed.
/// </para>
/// <para>
/// The two keys are not redundant, and phase 43 is why: a coordinator will be able to turn a
/// node's capabilities on and off, and <see cref="Allowed"/> is <b>the ceiling it can never
/// raise</b>. One boolean would collapse "the operator enabled tools" and "the hub may run any tool
/// present on this box" into a single consent, which is a coordinator compromise away from
/// fleet-wide RCE. Same shape as <c>Ollama:Supervisor:Enabled</c> vs <c>AutoInstall</c>
/// (phase-36 D6), <c>Auth:Clients[].Collections</c> (phase-31 D1) and <c>Fallback:ModelMap</c>
/// (phase-22 D5): the list <em>is</em> the grant.
/// </para>
/// </remarks>
public sealed class ToolOptions
{
    public const string SectionName = "Tools";

    /// <summary>
    /// Default <c>false</c>. With it off the node registers <c>NoToolRuntime</c>, spawns nothing,
    /// declares no tool capability, and is byte-identical in behaviour to v3.8.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The manifest ids this node may run. Empty means none — which, with
    /// <see cref="Enabled"/> on, is a configuration that does nothing and says so at startup.
    /// </summary>
    public List<string> Allowed { get; set; } = new();

    /// <summary>Where manifests are read from. Relative paths resolve against the working directory.</summary>
    public string ManifestDirectory { get; set; } = "tools";

    /// <summary>
    /// Where per-request scratch directories are created (D5). The node writes attachments here and
    /// deletes the directory in a <c>finally</c>, always.
    /// </summary>
    /// <remarks>
    /// <b>The fifth instance of the container permissions trap</b> (phase-21 D7, phase-30 D3,
    /// phase-38 D4, phase-39 D7): under <c>USER app</c> a default of <c>./data/...</c> resolves to
    /// <c>/app/data</c>, which that user cannot write. The default stays relative so a bare-metal
    /// or Windows node works out of the box, and the images set
    /// <c>Tools__ScratchDirectory=/data/tools/scratch</c> explicitly under the existing
    /// <c>chown app:app /data</c>.
    /// </remarks>
    public string ScratchDirectory { get; set; } = Path.Combine("data", "tools", "scratch");

    /// <summary>The attachment ceiling; over it is a 413 at the edge (phase-40 D4).</summary>
    public long MaxAttachmentBytes { get; set; } = ToolAttachmentLimits.DefaultMaxBytes;

    /// <summary>
    /// Whether a worker may fetch model weights it does not have (phase 42, D4). Default
    /// <c>false</c>; the <c>:tools</c> image sets it <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a <em>third</em> opt-in, and the third one is not redundant with the first two for the
    /// same reason the second was not redundant with the first (phase-41 D2, phase-36 D6):
    /// <c>Enabled</c> consents to running tools, <c>Allowed</c> consents to running <em>these</em>
    /// tools, and this consents to one of them reaching the internet from a box whose operator may
    /// have deliberately air-gapped it. Whisper auto-downloads its weights on first use, which is
    /// exactly the reach phase-39 D7 refused to do at boot.
    /// </para>
    /// <para>
    /// The <c>:tools</c> image sets it true because a tools image that cannot fetch a model is
    /// furniture, and choosing that image <em>is</em> the consent — the same reasoning by which the
    /// <c>:ollama</c> image sets <c>Ollama__Supervisor__Enabled=true</c> and a bare node does not.
    /// With it off, a worker that needs missing weights fails the <b>job</b> with a message naming
    /// this key and the exact pre-fetch command; the node keeps serving everything else.
    /// </para>
    /// </remarks>
    public bool AllowModelDownload { get; set; }

    /// <summary>
    /// What the node tells every worker about itself. Stated into the child's environment rather
    /// than inherited — the environment is cleared first (phase-41 D3), so this is the only way a
    /// consent flag reaches a worker, and a worker cannot pick one up by accident.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["INFERHUB_ALLOW_MODEL_DOWNLOAD"] = AllowModelDownload ? "1" : "0"
    };

    /// <summary>
    /// How long a request waits for a free worker before it is refused. Past it: <c>503</c> +
    /// <c>Retry-After</c> — the same status and header as the hub's <c>RequestQueue</c>
    /// (phase-25 D5) and solo mode's concurrency gate (phase-37 D9), so a client's retry logic
    /// behaves identically whichever limit it hit.
    /// </summary>
    public int QueueMaxWaitSeconds { get; set; } = 30;

    /// <summary>
    /// The restart budget, lifted from <c>Ollama:Supervisor</c> (phase-36 D4) rather than
    /// re-derived. Past the budget a pool stops starting workers, logs once at Error, withdraws its
    /// capabilities, and keeps probing — so a tool that recovers is noticed and one that does not
    /// cannot spin.
    /// </summary>
    public int MaxStartAttempts { get; set; } = 3;

    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Wait before the second and later attempts in a window; doubles each time.</summary>
    public TimeSpan RestartBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often a pool that has given up tries one worker anyway. The probe deliberately does not
    /// consume the budget — it <em>is</em> the probe — and a success resets everything.
    /// </summary>
    public TimeSpan RecoveryProbeInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often idle workers are retired and pools are probed.</summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsAllowed(string toolId) =>
        Allowed.Any(allowed => string.Equals(allowed?.Trim(), toolId, StringComparison.OrdinalIgnoreCase));

    public string ResolvedManifestDirectory() => Path.GetFullPath(ManifestDirectory);

    public string ResolvedScratchDirectory() => Path.GetFullPath(ScratchDirectory);
}
