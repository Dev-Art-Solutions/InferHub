using InferHub.Node.Configuration;

namespace InferHub.Node.Backends;

/// <summary>
/// One entry under <c>Backend:Engines:{name}</c> (phase 95): an engine this node may run, how to
/// reach it, and — for llama.cpp — how to launch it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two engines with a section of their own keep it.</b> An <c>ollama</c> engine is the
/// <c>Ollama:</c> section (endpoint, timeout, on-demand) and a <c>colibri</c> engine is the
/// <c>Colibri:</c> section (KV slots, <c>Serve</c>), so a node that grows a second engine changes no
/// key it already has. That is also why there is at most one of each (95 D2): two would need two
/// copies of a section that was written as one.
/// </para>
/// <para>
/// <c>llamacpp</c> and <c>openai</c> are the ones that repeat — one <c>llama-server</c> serves one
/// GGUF, so a box with three models runs three of them, each on its own port.
/// </para>
/// </remarks>
public sealed class EngineOptions
{
    /// <summary><c>ollama</c>, <c>llamacpp</c>, <c>colibri</c> or <c>openai</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Whether the engine runs when the node boots. <b>A coordinator can start one that is
    /// <c>false</c> here</b> — being listed is the grant, this is only the default (95 D4) — and stop
    /// one that is <c>true</c>.
    /// </summary>
    public bool Autostart { get; set; } = true;

    /// <summary>
    /// Where an already-running server listens. Not for <c>ollama</c> (that is <c>Ollama:Endpoint</c>),
    /// and not beside a launch — a launched engine listens where it was told to (93 D5's rule).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>Environment or user-secrets only, as for <c>Upstream:ApiKey</c>.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// <c>llamacpp</c> / <c>openai</c>: this server is an embedding server. <c>llama-server
    /// --embeddings</c> serves <em>only</em> embeddings, so the engine declares <c>embed</c> and not
    /// <c>chat</c> — declared, not discovered (67 D4) — and a launched one is started with the flag.
    /// </summary>
    public bool Embeddings { get; set; }

    /// <summary>Same include/exclude semantics as <c>Node:Models</c>, applied to this engine alone.</summary>
    public ModelFilterOptions Models { get; set; } = new();

    /// <summary><c>llamacpp</c> only: launch <c>llama-server</c> as this node's child.</summary>
    public EngineServeOptions Serve { get; set; } = new();

    public string NormalizedType() => (Type ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>
/// <c>Backend:Engines:{name}:Serve</c> — the node launches <c>llama-server</c> (95 D3). Off unless
/// <see cref="Model"/> is set: the path is the consent, as it is for <c>Colibri:Serve:Model</c>.
/// </summary>
public sealed class EngineServeOptions
{
    /// <summary>llama.cpp's own default port for <c>llama-server</c>.</summary>
    public const int LlamaCppDefaultPort = 8080;

    /// <summary>The <c>llama-server</c> binary, by path or on <c>PATH</c>.</summary>
    public string Executable { get; set; } = "llama-server";

    /// <summary>The GGUF file (<c>-m</c>). Unset: launch nothing.</summary>
    public string? Model { get; set; }

    /// <summary><c>--alias</c>: the name the hub routes on. Unset: the file's name without <c>.gguf</c>.</summary>
    public string? Alias { get; set; }

    public int Port { get; set; } = LlamaCppDefaultPort;

    /// <summary>
    /// Anything else the operator wants on the command line (<c>-c 8192</c>, <c>-ngl 99</c>), one
    /// token per entry. Never a shell string: there is no shell (41 D2's reason).
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Model);

    public string ResolvedAlias()
        => string.IsNullOrWhiteSpace(Alias)
            ? Path.GetFileNameWithoutExtension(Model!.Trim())
            : Alias!.Trim();

    public string LaunchedBaseUrl() => $"http://127.0.0.1:{Port}/v1";
}
