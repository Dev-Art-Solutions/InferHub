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
}
