namespace InferHub.Coordinator.Services;

/// <summary>
/// Cloud burst: when the fleet cannot serve a model, forward to a configured OpenAI-compatible
/// upstream instead of returning a flat 404.
/// </summary>
/// <remarks>
/// This is the feature most likely to do something its owner never agreed to. Sending someone's
/// prompt to a third party because their GPU box was asleep is a betrayal if it happens by
/// surprise. So: <see cref="Enabled"/> is <c>false</c> by default, only explicitly mapped models
/// are eligible, every fallback response carries <c>X-InferHub-Served-By: fallback</c>, the
/// status page counts them, and the coordinator stores neither the prompt nor the answer.
/// </remarks>
public sealed class FallbackOptions
{
    public const string SectionName = "Fallback";

    /// <summary>Fires only when a node holds no such model.</summary>
    public const string TriggerNoNode = "no-node";

    /// <summary>Also fires when every node holding it is at its declared concurrency cap.</summary>
    public const string TriggerNoNodeOrSaturated = "no-node-or-saturated";

    public bool Enabled { get; set; }

    public string? BaseUrl { get; set; }

    /// <summary>Environment or user-secrets only. Never <c>appsettings.json</c>.</summary>
    public string? ApiKey { get; set; }

    public string Trigger { get; set; } = TriggerNoNode;

    /// <summary>
    /// Local model name → upstream model name (<c>llama3</c> → <c>gpt-4o-mini</c>). A model that
    /// is not mapped is never sent upstream: the map *is* the consent.
    /// </summary>
    public Dictionary<string, string> ModelMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A narrower allowlist within the map. Empty means every mapped model.</summary>
    public List<string> AllowedModels { get; set; } = new();

    public int TimeoutSeconds { get; set; } = 300;

    public string NormalizedTrigger()
        => string.Equals(Trigger?.Trim(), TriggerNoNodeOrSaturated, StringComparison.OrdinalIgnoreCase)
            ? TriggerNoNodeOrSaturated
            : TriggerNoNode;
}
