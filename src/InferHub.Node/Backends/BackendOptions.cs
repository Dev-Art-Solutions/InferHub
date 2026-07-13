using InferHub.Node.Configuration;

namespace InferHub.Node.Backends;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    public const string Ollama = "ollama";
    public const string OpenAi = "openai";

    public string Type { get; set; } = Ollama;

    public string Normalized()
        => string.IsNullOrWhiteSpace(Type) ? Ollama : Type.Trim().ToLowerInvariant();
}

/// <summary>
/// The upstream OpenAI-compatible server a node drives when <c>Backend:Type=openai</c>: vLLM,
/// llama.cpp's server, LM Studio, TGI, or a hosted provider. Bound from the top-level
/// <c>OpenAi</c> section, alongside <c>Ollama</c>.
/// </summary>
public sealed class OpenAiBackendOptions
{
    public const string SectionName = "OpenAi";

    /// <summary>e.g. <c>http://localhost:8000/v1</c>. Required when <c>Backend:Type=openai</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Set this through the environment or user-secrets. Never <c>appsettings.json</c> — it is a
    /// credential for somebody else's service and it ends up in git the first time somebody is
    /// in a hurry.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Matches <see cref="OllamaOptions.RequestTimeout"/>'s reasoning: the coordinator's
    /// <c>Dispatcher:TimeoutSeconds</c> defaults to 300, and a node that gives up first turns a
    /// slow model into what looks like a node failure.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Same include/exclude semantics as <c>Node:Models</c>. Against a hosted provider an
    /// allowlist is effectively mandatory: the catalogue is hundreds of models the node cannot
    /// actually serve, and reporting all of them makes the coordinator route to it for anything.
    /// </summary>
    public ModelFilterOptions Models { get; set; } = new();
}
