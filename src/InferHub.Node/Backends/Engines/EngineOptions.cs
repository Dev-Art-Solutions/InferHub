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

    /// <summary>
    /// <c>llamacpp</c>: this server is a reranker (<c>llama-server --reranking</c>), so its model
    /// declares <c>rerank</c> (phase 96, D5) — declared, as <see cref="Embeddings"/> is. A router
    /// says it per model, in <c>Serve:Presets</c>.
    /// </summary>
    public bool Reranking { get; set; }

    /// <summary>
    /// <c>llamacpp</c> at a <see cref="BaseUrl"/>: that server is a <c>llama-server</c> router (started
    /// without <c>-m</c>), so this node may pull, load, unload and delete through it (96 D3). Declared,
    /// not probed (26). A router the node launches (<c>Serve:ModelsDir</c>/<c>Presets</c>) is one already.
    /// </summary>
    public bool Router { get; set; }

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

    /// <summary>The GGUF file (<c>-m</c>). Unset, with no router keys either: launch nothing.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// Router mode (phase 96, D1): a directory of GGUFs, each served under its file name and loaded on
    /// first use (<c>--models-dir</c>). A sub-directory holding a model and its <c>mmproj</c> is one
    /// multimodal model — llama.cpp's own convention.
    /// </summary>
    public string? ModelsDir { get; set; }

    /// <summary>
    /// Router mode: named models with settings of their own, written by the node into the INI
    /// <c>--models-preset</c> reads. The name is the routed model name. A name that is also a file in
    /// <see cref="ModelsDir"/> adds settings to that file.
    /// </summary>
    public Dictionary<string, LlamaCppPresetOptions> Presets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Router mode: how many models may be loaded at once (<c>--models-max</c>, LRU). Unset: llama.cpp's 4.</summary>
    public int? MaxLoaded { get; set; }

    /// <summary><c>--alias</c>: the name the hub routes on. Unset: the file's name without <c>.gguf</c>.</summary>
    public string? Alias { get; set; }

    public int Port { get; set; } = LlamaCppDefaultPort;

    /// <summary>
    /// Anything else the operator wants on the command line (<c>-c 8192</c>, <c>-ngl 99</c>), one
    /// token per entry. Never a shell string: there is no shell (41 D2's reason).
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>The node launches a <c>llama-server</c> — one model or a router.</summary>
    public bool IsEnabled => IsSingle || IsRouter;

    /// <summary>One GGUF behind <c>-m</c>: the v3.60 shape.</summary>
    public bool IsSingle => !string.IsNullOrWhiteSpace(Model);

    /// <summary>A router: many models, loaded on demand, managed through its own endpoints (96 D1).</summary>
    public bool IsRouter => !string.IsNullOrWhiteSpace(ModelsDir) || Presets.Count > 0;

    public string ResolvedAlias()
        => string.IsNullOrWhiteSpace(Alias)
            ? Path.GetFileNameWithoutExtension(Model!.Trim())
            : Alias!.Trim();

    public string LaunchedBaseUrl() => $"http://127.0.0.1:{Port}/v1";
}

/// <summary>
/// <c>Backend:Engines:{name}:Serve:Presets:{model}</c> (phase 96, D1) — one section of the INI a
/// router reads. Exactly one of <see cref="Model"/> and <see cref="HfRepo"/>, unless the name is a
/// file in <c>Serve:ModelsDir</c> and this only adds settings to it.
/// </summary>
public sealed class LlamaCppPresetOptions
{
    /// <summary>A GGUF on this box.</summary>
    public string? Model { get; set; }

    /// <summary><c>owner/repo[:quant]</c>, downloaded by llama.cpp on first load into its cache.</summary>
    public string? HfRepo { get; set; }

    /// <summary>An embedding model: it declares <c>embed</c> and nothing else (96 D2).</summary>
    public bool Embeddings { get; set; }

    /// <summary>A reranker: it declares <c>rerank</c> and nothing else (96 D2, D5).</summary>
    public bool Reranking { get; set; }

    /// <summary>The multimodal projector for a vision or audio model.</summary>
    public string? Mmproj { get; set; }

    /// <summary>
    /// Any other llama.cpp long option, without its dashes: <c>{"ctx-size": "8192", "n-gpu-layers":
    /// "99"}</c>. A plain token each — a newline here would open a section of somebody else's.
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
