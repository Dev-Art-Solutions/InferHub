namespace InferHub.Node.Configuration;

public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string Endpoint { get; set; } = "http://localhost:11434/";

    /// <summary>
    /// How long to wait on a single Ollama HTTP call.
    /// </summary>
    /// <remarks>
    /// Left unset, the call inherits <c>HttpClient</c>'s default of 100 seconds — while the
    /// coordinator's <c>Dispatcher:TimeoutSeconds</c> defaults to 300. The node would give up
    /// three minutes before the coordinator was willing to, so a model whose cold load ran past
    /// 100s (routine for a large model on a cold GPU box) surfaced as a 502 that looked like
    /// the node had failed. Default to the coordinator's patience; raise it for very large models.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Fail startup when no CUDA device is visible to this process, instead of reporting it and
    /// running on the CPU (phase 39, D6). Default <c>false</c> — <em>including</em> in the bundled
    /// image, which supports a CPU-only mode on purpose.
    /// </summary>
    /// <remarks>
    /// For the operator who wants the guarantee rather than the report: a fleet node whose whole
    /// purpose is the card should not quietly become a slow one because a <c>--gpus</c> flag fell
    /// out of a systemd unit. What is never acceptable is <em>silence</em>, and that is handled
    /// for everyone by <see cref="Backends.GpuReport"/> logging what it found either way.
    /// </remarks>
    public bool RequireGpu { get; set; }
}
