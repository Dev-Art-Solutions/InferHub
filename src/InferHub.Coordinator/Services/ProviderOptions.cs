namespace InferHub.Coordinator.Services;

/// <summary>
/// Named cloud providers (phase 61). Where <see cref="FallbackOptions"/> is one anonymous upstream,
/// this is a map of them: each with its own credential, its own models and its own trigger.
/// </summary>
/// <remarks>
/// The consent model is <b>unchanged</b> from 22 D5 and is the one thing this feature may not
/// weaken: a model that is not named in some provider's <see cref="ProviderDefinition.ModelMap"/>
/// never leaves the fleet. What changes is only that there can now be more than one place it may go
/// — and <see cref="ProviderOptionsValidator"/> refuses to start if two of them claim the same model,
/// because "whose servers see this prompt" must never be decided by dictionary ordering (61 D1).
/// </remarks>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    /// <summary>Provider id → definition. The id is the operator's own word for it.</summary>
    public Dictionary<string, ProviderDefinition> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One configured provider.</summary>
public sealed class ProviderDefinition
{
    /// <summary>
    /// Every server that speaks OpenAI's wire format: OpenAI itself, OpenRouter, vLLM, LM Studio,
    /// TGI. Phases 63 and 64 add <c>anthropic</c> and <c>gemini</c>; until then an unknown value is
    /// a startup failure rather than a provider that silently never fires.
    /// </summary>
    public const string TypeOpenAiCompatible = "openai-compatible";

    /// <summary>
    /// OpenRouter (phase 62). <b>The same dialect</b> — <c>ProviderDispatcher</c> hands both types
    /// to <see cref="InferHub.Shared.OpenAi.OpenAiUpstreamClient"/> — and a type of its own anyway,
    /// because three things about it are not the dialect: a base URL nobody should have to type, an
    /// attribution header set, and a model id shape that is checked at startup.
    /// </summary>
    public const string TypeOpenRouter = "openrouter";

    /// <summary>Used when a <c>Type: openrouter</c> provider names no <see cref="BaseUrl"/>.</summary>
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Anthropic's own <c>/v1/messages</c> (phase 63) — a real second dialect, unlike
    /// <see cref="TypeOpenRouter"/>. The credential is <c>x-api-key</c> rather than a Bearer token,
    /// the version header is required, and <see cref="MaxTokens"/> exists because the vendor
    /// requires a field Ollama has no equivalent for.
    /// </summary>
    public const string TypeAnthropic = "anthropic";

    /// <summary>Used when a <c>Type: anthropic</c> provider names no <see cref="BaseUrl"/>.</summary>
    public const string AnthropicBaseUrl = "https://api.anthropic.com/v1";

    /// <summary>
    /// Gemini's own <c>:generateContent</c> (phase 64) — the fourth type and the third dialect. The
    /// credential is <c>x-goog-api-key</c>, the model is a <b>path segment</b> rather than a body
    /// field, and streaming needs <c>?alt=sse</c> or the endpoint answers with a JSON array.
    /// </summary>
    public const string TypeGemini = "gemini";

    /// <summary>Used when a <c>Type: gemini</c> provider names no <see cref="BaseUrl"/>.</summary>
    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    /// <summary>
    /// Off is off: a disabled provider maps nothing, so its models are simply not eligible. It is
    /// here so an operator can park a provider without deleting the map they spent time on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Type { get; set; } = TypeOpenAiCompatible;

    public string? BaseUrl { get; set; }

    /// <summary>Environment or user-secrets only (<c>Providers__openrouter__ApiKey</c>). Never <c>appsettings.json</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Per provider, not per hub (61 D5). Whether you want <em>this</em> upstream when the fleet is
    /// merely busy is a question about that upstream's price and latency, and a single global answer
    /// forces the expensive vendor and the cheap one into one policy.
    /// </summary>
    public string Trigger { get; set; } = FallbackOptions.TriggerNoNode;

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// <c>HTTP-Referer</c>, sent only to an <see cref="TypeOpenRouter"/> provider and only when set.
    /// </summary>
    /// <remarks>
    /// This and <see cref="Title"/> put an app on OpenRouter's <b>public</b> rankings, which is why
    /// neither has a default (62 D2). Filling them in with this product's own name and URL would be
    /// free marketing paid for with somebody else's deployment appearing on a vendor's public page
    /// because they configured a model. Not the caller's content, but a fact about the caller's
    /// infrastructure — so the sending is the operator's sentence, not ours.
    /// </remarks>
    public string? Referer { get; set; }

    /// <summary><c>X-OpenRouter-Title</c>. See <see cref="Referer"/> — opt-in, no default.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// The <c>anthropic-version</c> header. Overridable so a deployment can pin one; defaulted so
    /// nobody has to type it. <see cref="TypeAnthropic"/> only.
    /// </summary>
    public string? AnthropicVersion { get; set; }

    /// <summary>
    /// The <c>max_tokens</c> an Anthropic request carries when the caller named none (63 D2).
    /// Anthropic requires the field; Ollama has no equivalent to carry, so it is <b>declared</b>
    /// here rather than detected — and a caller's <c>options.num_predict</c> always wins.
    /// </summary>
    /// <remarks>
    /// It is a ceiling, not a target: the model stops at <c>end_turn</c> long before it, and when it
    /// does not, <c>stop_reason: max_tokens</c> becomes Ollama's <c>done_reason: length</c>, which is
    /// a thing clients already read. Raise it here when long answers arrive truncated — a number an
    /// operator cannot see is a number they cannot raise.
    /// </remarks>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// <c>generationConfig.thinkingConfig.thinkingBudget</c>, sent only when set.
    /// <see cref="TypeGemini"/> only (64 D6).
    /// </summary>
    /// <remarks>
    /// Gemini models think by default and the thinking tokens are <b>billed as output</b> while
    /// being reported separately from the answer's — so `eval_count` carries
    /// <c>candidatesTokenCount</c> alone and the invoice is larger than it. This is the lever that
    /// closes the gap rather than an arithmetic that hides it: <c>0</c> disables thinking on the
    /// models that allow it, absent leaves the vendor's dynamic default. It is deliberately not
    /// defaulted to <c>0</c> — turning off a model's reasoning is a quality decision, and making it
    /// for an operator who never asked would be a worse surprise than the bill.
    /// </remarks>
    public int? ThinkingBudget { get; set; }

    /// <summary>Local model name → this provider's name for it. The map is the consent.</summary>
    public Dictionary<string, string> ModelMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A narrower allowlist within the map. Empty means every mapped model.</summary>
    public List<string> AllowedModels { get; set; } = new();

    public string NormalizedTrigger()
        => string.Equals(Trigger?.Trim(), FallbackOptions.TriggerNoNodeOrSaturated, StringComparison.OrdinalIgnoreCase)
            ? FallbackOptions.TriggerNoNodeOrSaturated
            : FallbackOptions.TriggerNoNode;

    public string NormalizedType()
        => string.IsNullOrWhiteSpace(Type) ? TypeOpenAiCompatible : Type.Trim().ToLowerInvariant();

    /// <summary>
    /// The base URL to point an <c>HttpClient</c> at: the operator's when they named one, and
    /// OpenRouter's own when the type supplies it. Still overridable — a proxy in front of a vendor
    /// is a deployment somebody has, and a default that cannot be replaced is a wall.
    /// </summary>
    public string? ResolvedBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            return BaseUrl!.Trim();
        }

        return NormalizedType() switch
        {
            TypeOpenRouter => OpenRouterBaseUrl,
            TypeAnthropic => AnthropicBaseUrl,
            TypeGemini => GeminiBaseUrl,
            _ => null
        };
    }

    /// <summary>The pinned <c>anthropic-version</c>, or the one this release was written against.</summary>
    public string ResolvedAnthropicVersion()
        => string.IsNullOrWhiteSpace(AnthropicVersion)
            ? InferHub.Shared.Anthropic.AnthropicUpstreamClient.DefaultVersion
            : AnthropicVersion!.Trim();
}

/// <summary>
/// A provider resolved for one model: who serves it, what they call it, and what the response
/// header says.
/// </summary>
/// <param name="Legacy">
/// True for the provider projected from the <c>Fallback:</c> section (61 D2). It exists so the
/// header keeps saying <c>fallback</c> for a deployment that never wrote a <c>Providers:</c> block —
/// a value somebody's dashboard, log filter or <c>curl -i</c> habit already keys on (61 D4).
/// </param>
public sealed record ProviderRoute(
    string Id,
    ProviderDefinition Definition,
    string UpstreamModel,
    bool Legacy)
{
    /// <summary>The <c>X-InferHub-Served-By</c> value for a response this provider served.</summary>
    public string ServedBy => Legacy ? "fallback" : $"provider:{Id}";
}
