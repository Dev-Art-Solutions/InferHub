namespace InferHub.Shared.Upstream;

/// <summary>
/// The base URLs the three vendor dialects default to when a deployment names none. Phase 67.
/// </summary>
/// <remarks>
/// <para>
/// They lived on <c>ProviderDefinition</c> from phases 62–64, which was the only host that had them.
/// Phase 67 gives the node the same three types, and a URL written down twice is a URL corrected
/// once — so the authority moved here, beside the clients that talk to them, and both ends point at
/// it. Nothing else moved: which <em>type name</em> reaches these is each host's own vocabulary
/// (the hub says <c>openai-compatible</c>, the node has said <c>openai</c> since phase 22).
/// </para>
/// <para>
/// Each is still overridable wherever it is used — a proxy in front of a vendor is a deployment
/// somebody has, and a default that cannot be replaced is a wall (62 D3).
/// </para>
/// </remarks>
public static class UpstreamDefaults
{
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    public const string AnthropicBaseUrl = "https://api.anthropic.com/v1";

    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
}
