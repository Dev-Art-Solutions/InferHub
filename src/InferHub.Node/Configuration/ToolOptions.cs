using System.Globalization;
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

    /// <summary>Image generation (phase 46). Inert unless an image worker is loaded.</summary>
    public ImageToolOptions Image { get; set; } = new();

    /// <summary>
    /// What the node tells every worker about itself. Stated into the child's environment rather
    /// than inherited — the environment is cleared first (phase-41 D3), so this is the only way a
    /// consent flag reaches a worker, and a worker cannot pick one up by accident.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment()
    {
        var environment = BaseWorkerEnvironment();

        // Only when it is set. An empty HF_TOKEN is not the same as no HF_TOKEN to
        // `huggingface_hub`: it sends the blank one and gets a 401 on a repo that would have been
        // readable anonymously, which reads as "the model is gone" rather than "your token is empty".
        if (!string.IsNullOrWhiteSpace(Image.HuggingFaceToken))
        {
            environment["HF_TOKEN"] = Image.HuggingFaceToken.Trim();
        }

        return environment;
    }

    private Dictionary<string, string> BaseWorkerEnvironment() => new(StringComparer.Ordinal)
    {
        ["INFERHUB_ALLOW_MODEL_DOWNLOAD"] = AllowModelDownload ? "1" : "0",

        // Phase 46. A diffusion worker decides for itself whether it may run on a CPU, and it can
        // only do that if the node tells it — the environment is cleared before spawn, so an
        // operator's key would otherwise never reach the process that has to honour it.
        ["INFERHUB_IMAGE_REQUIRE_GPU"] = Image.RequireGpu ? "1" : "0",
        ["INFERHUB_IMAGE_ALLOW_SLOW_CPU"] = Image.AllowSlowCpu ? "1" : "0",
        ["INFERHUB_IMAGE_RECIPES"] = Image.RecipeDirectory ?? string.Empty,

        // Phase 48. The licence grant reaches the worker the same way, and for the same reason: it
        // is DEFENCE IN DEPTH rather than a duplicate. The node refuses to declare an unaccepted
        // recipe so the hub never routes at one; the worker refuses to download or load it, which is
        // the lock on the process that would actually do those things. A solo caller naming the
        // recipe directly meets the second one.
        ["INFERHUB_IMAGE_ACCEPTED_LICENSES"] = string.Join(
            ",",
            Image.AcceptedLicenses.Where(entry => !string.IsNullOrWhiteSpace(entry)).Select(entry => entry.Trim())),

        ["INFERHUB_IMAGE_RESIDENT_RECIPES"] = Math.Max(1, Image.ResidentRecipes).ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// How long a request waits for a free worker before it is refused. Past it: <c>503</c> +
    /// <c>Retry-After</c> — the same status and header as the hub's <c>RequestQueue</c>
    /// (phase-25 D5) and solo mode's concurrency gate (phase-37 D9), so a client's retry logic
    /// behaves identically whichever limit it hit.
    /// </summary>
    public int QueueMaxWaitSeconds { get; set; } = 30;

    /// <summary>
    /// How long a worker gets to honour a <c>cancel</c> frame before it is terminated and restarted
    /// (phase 47, D3). Default 20s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Killing first would be simpler and is wrong.</b> A diffusion worker holds weights that took
    /// tens of seconds to load — SDXL today, and twelve to twenty gigabytes taking a minute or more
    /// once the catalogue grows. Killing it to abandon one job punishes the <em>next</em> caller for
    /// the first one's change of mind, and the punishment gets worse with every model added.
    /// </para>
    /// <para>
    /// Past this budget the worker is by definition not cooperating, and phase-41 deviation 6's
    /// instinct applies unchanged: terminate, release the slot only once the process is gone, and
    /// start a fresh one. The two are complementary cases rather than a contradiction.
    /// </para>
    /// </remarks>
    public int CancelGraceSeconds { get; set; } = 20;

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

/// <summary>
/// <c>Tools:Image</c> — the keys an image worker honours (phase 46). Every one of them is read by
/// the <em>worker</em>; the node's job is to state them into the child's environment, because the
/// environment is cleared before spawn (phase-41 D3) and nothing else would reach it.
/// </summary>
/// <remarks>
/// The node deliberately learns nothing about diffusion. Rule 1's shape one level out: the runtime
/// knows how to start a process, write a line and read a line, and which of those lines mean
/// "cuda" is the worker's business (phase-41 D1).
/// </remarks>
public sealed class ImageToolOptions
{
    /// <summary>
    /// Default <c>true</c>: an image worker refuses to start when no CUDA device is reachable, and
    /// says why and which key to unset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <b>phase-35 D4 vs phase-37 D4</b>, applied to a number. A tool that loads happily on
    /// a CPU and then serves four-minute requests is not a slow feature, it is a node the fleet will
    /// keep routing to: the hub sees a healthy capability and every caller pays for the discovery.
    /// A refusal at startup costs one log line and is read by the person who can fix it.
    /// </para>
    /// <para>
    /// Unset it and only recipes the worker marks <c>cpuViable</c> are declared — SD 1.5 at 512², and
    /// not SDXL at 1024². <see cref="AllowSlowCpu"/> is the third step for an operator who has read
    /// both numbers and wants the slow one anyway: their hardware, their call, with a warning per
    /// job.
    /// </para>
    /// </remarks>
    public bool RequireGpu { get; set; } = true;

    /// <summary>Declare CPU-hostile recipes on a CPU-only box anyway. Off, and loud when on.</summary>
    public bool AllowSlowCpu { get; set; }

    /// <summary>
    /// Licence ids this operator has read and accepted (phase 48, D5). A recipe whose
    /// <c>license.permissive</c> is not <c>true</c> is <b>loaded, logged by name and not started</b>
    /// unless its licence id appears here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the fourth opt-in and it is not redundant with the other three.</b>
    /// <c>Tools:Enabled</c> consents to the feature, <c>Tools:Allowed</c> consents to <em>these
    /// tools</em>, <c>Tools:AllowModelDownload</c> consents to reaching the internet — and none of
    /// them says "and I accept the Stability AI Non-Commercial Research Community License". None of
    /// this is legal advice; it is a refusal to make a licence decision on the operator's behalf and
    /// silently, which is the only part of it that is ours to get right.
    /// </para>
    /// <para>
    /// <b>A list, not a boolean</b>, for phase-41 D2's reason exactly: <c>sd35-medium</c> is free for
    /// most people who will run it and <c>sdxl-turbo</c> is not usable commercially at all, so a
    /// single <c>AcceptNonPermissiveLicenses=true</c> would let somebody who read one licence enable
    /// both. Per item, or the grant means nothing.
    /// </para>
    /// <para>
    /// <b>It names licence ids rather than recipe ids.</b> What is being accepted is a licence — you
    /// read it once and it covers every model under it — and the key's name says so. The refusal
    /// prints the exact string to add and a link to the text.
    /// </para>
    /// </remarks>
    public List<string> AcceptedLicenses { get; set; } = new();

    /// <summary>
    /// How many recipes may be resident on the card at once (phase 48, D3). Default <b>1</b>.
    /// </summary>
    /// <remarks>
    /// A box with 48 GB genuinely can hold SDXL and FLUX together and should not thrash. The default
    /// is 1 for phase-41 D4's reason: the expensive default is the one nobody realises they chose,
    /// and two multi-gigabyte pipelines on one card is an out-of-memory error at the worst possible
    /// moment. Raising it is knowing.
    /// </remarks>
    public int ResidentRecipes { get; set; } = 1;

    /// <summary>
    /// A Hugging Face read token, for a <b>gated</b> repository (phase 48). Empty by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It has to be a key rather than an inherited variable, and that is phase-41 D3 rather than an
    /// oversight: the node <em>clears</em> the child's environment before spawn, so
    /// <c>docker run -e HF_TOKEN=…</c> reaches the node and stops there. Stating it here is the only
    /// way it reaches the process that needs it, and it is the same shape
    /// <c>INFERHUB_ALLOW_MODEL_DOWNLOAD</c> already has.
    /// </para>
    /// <para>
    /// It is a <em>separate thing</em> from <see cref="AcceptedLicenses"/>, and conflating them
    /// would be wrong in both directions: accepting a licence tells <b>this node</b> it may run a
    /// model, and a token is how <b>Hugging Face</b> decides whether to hand the weights over. Of
    /// what ships, only <c>sd35-medium</c> needs one.
    /// </para>
    /// </remarks>
    public string? HuggingFaceToken { get; set; }

    /// <summary>Whether a licence id has been accepted. A blank entry never counts.</summary>
    public bool AcceptsLicense(string licenseId) =>
        AcceptedLicenses.Any(entry => !string.IsNullOrWhiteSpace(entry)
            && string.Equals(entry.Trim(), licenseId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where model recipes are read from. Empty means the worker's own default, which in the
    /// <c>:diffusion</c> image is <c>/opt/inferhub/recipes</c>.
    /// </summary>
    /// <remarks>
    /// A recipe is a <em>model</em>; a manifest is a <em>tool</em>. They are two files on purpose
    /// (phase-46 D3): the manifest is the operator's ceiling and is what <c>Tools:Allowed</c> names,
    /// while recipes are a catalogue the tool reads. Collapsing them would make every new model a
    /// new entry in <c>Tools:Allowed</c>, and a phase-43 profile could then not enable a model
    /// without the operator having pre-named it — which is the wrong ceiling: the operator consented
    /// to running the diffusion tool, and which of its models are on is exactly what a profile is
    /// for.
    /// </remarks>
    public string? RecipeDirectory { get; set; }
}
