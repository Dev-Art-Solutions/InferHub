using InferHub.Shared.Contracts;

namespace InferHub.Shared.CloudMedia;

/// <summary>
/// The two vendor dialects a cloud picture, voice or transcript can come from (104 D3). A plain
/// string on purpose — it is the value an operator writes in <c>Type</c>.
/// </summary>
public static class CloudMediaDialects
{
    /// <summary>OpenAI's Images and Audio APIs, and anything that speaks them.</summary>
    public const string OpenAi = "openai";

    /// <summary>Gemini's <c>:generateContent</c> and Imagen's <c>:predict</c> on the Gemini API.</summary>
    public const string Gemini = "gemini";

    public const string OpenAiBaseUrl = "https://api.openai.com/v1";

    public static string? DefaultBaseUrl(string dialect) => dialect switch
    {
        OpenAi => OpenAiBaseUrl,
        Gemini => Upstream.UpstreamDefaults.GeminiBaseUrl,
        _ => null
    };

    /// <summary>
    /// The capabilities a cloud provider can serve in this release. Video is phase 105; a map naming
    /// it fails startup rather than declaring a kind nothing here can answer.
    /// </summary>
    public static readonly IReadOnlyList<string> Capabilities =
    [
        CapabilityKinds.Image,
        CapabilityKinds.ImageEdit,
        CapabilityKinds.Speak,
        CapabilityKinds.Transcribe
    ];

    public static bool IsSupportedCapability(string? capability) =>
        Capabilities.Contains(capability ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One cloud media provider as a node configures it (<c>CloudMedia:Providers:{id}</c>). The hub's
/// <c>Providers:Entries:{id}:Media</c> is the same map on its own provider block and is projected
/// onto this shape, so both hosts resolve routes through <see cref="CloudMediaCatalog"/>.
/// </summary>
/// <remarks>
/// A plain options class (rule 2): no <c>IOptions</c>, no validation attributes. Each host validates
/// it with its own <c>IValidateOptions</c>, calling <see cref="CloudMediaCatalog.Validate"/>.
/// </remarks>
public sealed class CloudMediaProviderDefinition
{
    /// <summary>Off is off: a disabled provider maps nothing and declares nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>openai</c> or <c>gemini</c>.</summary>
    public string Type { get; set; } = CloudMediaDialects.OpenAi;

    /// <summary>Absent means the vendor's own (<see cref="CloudMediaDialects.DefaultBaseUrl"/>).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Environment or user-secrets only (<c>CloudMedia__Providers__openai__ApiKey</c>). Never in a file in git.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Capability → (local model name → this vendor's model id). The local name is the consent (104 D2).</summary>
    public Dictionary<string, Dictionary<string, string>> Media { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string NormalizedType() =>
        string.IsNullOrWhiteSpace(Type) ? CloudMediaDialects.OpenAi : Type.Trim().ToLowerInvariant();

    public string? ResolvedBaseUrl() =>
        string.IsNullOrWhiteSpace(BaseUrl) ? CloudMediaDialects.DefaultBaseUrl(NormalizedType()) : BaseUrl.Trim();
}

/// <summary>
/// A cloud provider resolved for one (capability, model): where to send it and what the vendor calls it.
/// </summary>
/// <remarks>The key is carried here and nowhere else; <see cref="ToString"/> leaves it out so a log line cannot.</remarks>
public sealed record CloudMediaRoute(
    string ProviderId,
    string Dialect,
    string BaseUrl,
    string? ApiKey,
    string Capability,
    string LocalModel,
    string UpstreamModel,
    int TimeoutSeconds)
{
    /// <summary>The <c>X-InferHub-Served-By</c> value, the same spelling a chat provider uses (61 D4).</summary>
    public string ServedBy => $"provider:{ProviderId}";

    public override string ToString() =>
        $"{ProviderId} ({Dialect}) {Capability}/{LocalModel} -> {UpstreamModel}";
}
