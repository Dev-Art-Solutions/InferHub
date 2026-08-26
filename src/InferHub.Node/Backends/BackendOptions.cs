using InferHub.Node.Configuration;
using InferHub.Shared.Upstream;

namespace InferHub.Node.Backends;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    public const string Ollama = "ollama";

    /// <summary>
    /// Any server that speaks OpenAI's wire format: vLLM, llama.cpp's server, LM Studio, TGI, or a
    /// hosted provider. The node's spelling of what the hub calls <c>openai-compatible</c> — kept as
    /// it was written in phase 22, because renaming it would be a config break for every deployment
    /// that has one.
    /// </summary>
    public const string OpenAi = "openai";

    /// <summary>OpenRouter (phase 67). The OpenAI dialect, its own base URL and attribution headers.</summary>
    public const string OpenRouter = "openrouter";

    /// <summary>Anthropic's own <c>/v1/messages</c> (phase 67, the dialect from 63).</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Gemini's own <c>:generateContent</c> (phase 67, the dialect from 64).</summary>
    public const string Gemini = "gemini";

    /// <summary>
    /// The four types driven by <see cref="UpstreamBackend"/> — everything that is not the local
    /// Ollama. Kept as a list so the validator, the composition root and the supervisor guard all
    /// ask the same question rather than each writing their own <c>!= ollama</c>.
    /// </summary>
    public static readonly string[] UpstreamTypes = [OpenAi, OpenRouter, Anthropic, Gemini];

    public string Type { get; set; } = Ollama;

    public string Normalized()
        => string.IsNullOrWhiteSpace(Type) ? Ollama : Type.Trim().ToLowerInvariant();

    /// <summary>Whether this type is served by <see cref="UpstreamBackend"/> rather than Ollama.</summary>
    public bool IsUpstream() => UpstreamTypes.Contains(Normalized());

    /// <summary>
    /// The three types phase 67 added — the ones whose vendor publishes a catalogue this node cannot
    /// possibly serve all of, and which therefore require an allowlist (67 D5).
    /// </summary>
    public bool IsVendor() => Normalized() is OpenRouter or Anthropic or Gemini;
}

/// <summary>
/// The upstream this node drives when <c>Backend:Type</c> is anything but <c>ollama</c>: a local
/// vLLM or llama.cpp server, or one of the three cloud vendors phase 67 added.
/// </summary>
/// <remarks>
/// <para>
/// <b>The section is <c>Upstream:</c>, and <c>OpenAi:</c> still binds</b> (67 D3). Every deployment
/// written against v2.4 through v3.34 configured this under <c>OpenAi:</c>; that keeps working, is
/// projected onto this same class at startup, and a node that changes no config is byte-identical.
/// What the new name buys is that the vendor keys below can exist at all — <c>OpenAi:MaxTokens</c>
/// and <c>OpenAi:AnthropicVersion</c> are the kind of key somebody screenshots.
/// </para>
/// <para>
/// A key written in <em>both</em> sections with different values is a <b>startup failure naming
/// both</b> (<see cref="Configuration.UpstreamBackendOptionsValidator"/>): which upstream receives a
/// prompt must not be decided by which section a binder happened to apply last. 65 D1's rule, one
/// host over.
/// </para>
/// </remarks>
public sealed class UpstreamBackendOptions
{
    public const string SectionName = "Upstream";

    /// <summary>The pre-67 name. Still bound, still projected, never removed without a major.</summary>
    public const string LegacySectionName = "OpenAi";

    /// <summary>
    /// e.g. <c>http://localhost:8000/v1</c>. Required for <c>Backend:Type=openai</c>; optional for
    /// the three vendor types, which each have a published one (see <see cref="ResolvedBaseUrl"/>).
    /// </summary>
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
    /// Same include/exclude semantics as <c>Node:Models</c>. Against <c>openai</c> it is optional
    /// and usually unnecessary — one vLLM serves one model. Against the three vendor types it is
    /// <b>required and enforced at startup</b> (67 D5): OpenRouter lists 419 ids and Gemini around
    /// fifty, embed-only and image ones among them, and a node that reported the catalogue would be
    /// telling the hub it can chat with an image model.
    /// </summary>
    public ModelFilterOptions Models { get; set; } = new();

    /// <summary>
    /// The <c>anthropic-version</c> header. Overridable so a deployment can pin one; defaulted so
    /// nobody has to type it. <c>Backend:Type=anthropic</c> only.
    /// </summary>
    public string? AnthropicVersion { get; set; }

    /// <summary>
    /// The <c>max_tokens</c> an Anthropic request carries when the caller named none (63 D2).
    /// Anthropic requires the field; Ollama has no equivalent to carry, so it is declared here
    /// rather than detected — and a caller's <c>options.num_predict</c> always wins.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// <c>generationConfig.thinkingConfig.thinkingBudget</c>, sent only when set.
    /// <c>Backend:Type=gemini</c> only (64 D6). <c>0</c> disables thinking on the models that allow
    /// it; absent leaves the vendor's dynamic default.
    /// </summary>
    public int? ThinkingBudget { get; set; }

    /// <summary>
    /// <c>HTTP-Referer</c>, sent only to an <see cref="BackendOptions.OpenRouter"/> upstream and only
    /// when set. This and <see cref="Title"/> put a deployment on OpenRouter's <b>public</b>
    /// rankings, which is why neither has a default (62 D2).
    /// </summary>
    public string? Referer { get; set; }

    /// <summary><c>X-OpenRouter-Title</c>. See <see cref="Referer"/> — opt-in, no default.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The base URL to point an <c>HttpClient</c> at: the operator's when they named one, and the
    /// vendor's own when the type supplies it. Null for <c>openai</c> with nothing configured, which
    /// is the startup failure the validator raises.
    /// </summary>
    public string? ResolvedBaseUrl(string backendType)
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            return BaseUrl!.Trim();
        }

        return backendType switch
        {
            BackendOptions.OpenRouter => UpstreamDefaults.OpenRouterBaseUrl,
            BackendOptions.Anthropic => UpstreamDefaults.AnthropicBaseUrl,
            BackendOptions.Gemini => UpstreamDefaults.GeminiBaseUrl,
            _ => null
        };
    }

    /// <summary>The pinned <c>anthropic-version</c>, or the one this release was written against.</summary>
    public string ResolvedAnthropicVersion()
        => string.IsNullOrWhiteSpace(AnthropicVersion)
            ? InferHub.Shared.Anthropic.AnthropicUpstreamClient.DefaultVersion
            : AnthropicVersion!.Trim();
}
