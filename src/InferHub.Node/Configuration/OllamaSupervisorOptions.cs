namespace InferHub.Node.Configuration;

/// <summary>
/// Governs the node's supervision of its <em>own</em> Ollama (phase 36). Off by default:
/// restarting a process on somebody's machine is a real side effect, so it is consented to
/// with a key rather than discovered.
/// </summary>
public sealed class OllamaSupervisorOptions
{
    public const string SectionName = "Ollama:Supervisor";

    /// <summary>
    /// Consents to <em>restarting</em> a local Ollama. It does not consent to installing one —
    /// that is <see cref="AutoInstall"/>, deliberately a second switch.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Watch the backend and report its health to the coordinator (phase 69, D4). <b>Default
    /// true</b>, and deliberately separate from <see cref="Enabled"/>: restarting somebody else's
    /// inference server needs consent and needs to be local, while <em>asking</em> a server whether
    /// it is alive is what the next request does anyway.
    /// </summary>
    /// <remarks>
    /// Applies to an <c>ollama</c>-typed node only, loopback or not. A vendor-typed node has no free
    /// liveness endpoint we may assume across four vendors, and a probe every
    /// <see cref="ProbeInterval"/> against a cloud vendor is a billed request. Off means the node
    /// sends no health at all, and the hub routes to it exactly as it did before v3.36.
    /// </remarks>
    public bool Watch { get; set; } = true;

    public TimeSpan ProbeInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Start Ollama immediately at host startup when nothing is listening, instead of waiting out
    /// <see cref="UnhealthyThreshold"/> probes first (phase 39, D4). Default <c>false</c>; the
    /// bundled image sets it, because in that container we <em>own</em> the Ollama and know it is
    /// not running yet — a cold start would otherwise idle for three probe intervals.
    /// </summary>
    /// <remarks>
    /// Applies only to <c>Unreachable</c> — the socket never opened. Never to
    /// a wedge: the threshold exists to avoid misdiagnosing a process that is running-but-slow, and
    /// something already answering the port at boot is exactly where guessing costs the most
    /// (phase-36 D3 — starting a wedged Ollama fails on a bound port, and the log blames the wrong
    /// thing).
    /// </remarks>
    public bool StartAtBoot { get; set; }

    /// <summary>
    /// Stop the Ollama <em>this supervisor spawned</em> when the node shuts down (phase 39, D4).
    /// Default <c>false</c>; the bundled image sets it.
    /// </summary>
    /// <remarks>
    /// Off by default because on a desktop the operator may still be using that Ollama after the
    /// node stops. On is right in a container, where the spawned process is a child of PID 1 that
    /// gets SIGKILLed the instant PID 1 exits — a <c>docker stop</c> during an <c>ollama pull</c>
    /// otherwise leaves a partial blob in the volume. It kills only what we started: never a
    /// service, never a pre-existing <c>ollama</c> found by name.
    /// </remarks>
    public bool StopOnShutdown { get; set; }

    /// <summary>
    /// The probe's own deadline, and the whole reason this feature works.
    /// </summary>
    /// <remarks>
    /// <c>Ollama:RequestTimeout</c> is five minutes on purpose (a cold 70B load). Probing over
    /// that budget would mean a <em>wedged</em> Ollama — the exact case this exists for — takes
    /// five minutes to produce one failed probe, and three of those to cross the threshold: a
    /// quarter of an hour before the node lifts a finger. Hence a separate, short deadline over
    /// a separate <c>HttpClient</c>. The two clients are not redundant; do not consolidate them.
    /// </remarks>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Consecutive failed probes before a state is declared. A GC pause, a saturated box
    /// mid-load or a laptop waking from sleep is not a wedge; any success resets the count.
    /// </summary>
    public int UnhealthyThreshold { get; set; } = 3;

    /// <summary>
    /// How long to wait for a restarted Ollama to answer. Generous, because a service that
    /// starts by loading a model is slow, not broken.
    /// </summary>
    public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public int MaxRestartAttempts { get; set; } = 3;

    public TimeSpan RestartWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Wait before the second and later attempts in a window; doubles each time.</summary>
    public TimeSpan RestartBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Downloads and runs the official installer when Ollama is genuinely <em>absent</em>
    /// (never when it is merely not answering), once per process lifetime.
    /// </summary>
    public bool AutoInstall { get; set; }

    /// <summary>Empty = the official channel for this platform. Point it at a mirror for an
    /// air-gapped or policy-managed fleet rather than reaching the internet from a GPU box.</summary>
    public string InstallUrl { get; set; } = string.Empty;

    /// <summary>Empty = discover (<c>Ollama</c> on Windows, <c>ollama.service</c> under systemd).</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Empty = discover <c>ollama</c> on <c>PATH</c>.</summary>
    public string ExecutablePath { get; set; } = string.Empty;
}
