using System.Text.Json.Serialization;

namespace InferHub.Shared.Gemini;

/// <summary>
/// Gemini's <c>:generateContent</c> body (phase 64). Every field here was read from the vendor's
/// current documentation on the day the phase was written, not recalled — 57's precedent, which is
/// in this repository because two of that phase's recalled facts were wrong.
/// </summary>
/// <remarks>
/// Three shapes are not Anthropic's and not OpenAI's: the turns are <c>contents[].parts[]</c> with
/// roles <c>user</c> and <c>model</c>, the system prompt is a <see cref="GeminiContent"/> rather
/// than a string, and every sampling knob lives under <c>generationConfig</c> instead of at the top
/// level. <b>The model id is not in the body at all</b> — it is a path segment (64 D2).
/// </remarks>
public sealed class GeminiGenerateRequest
{
    [JsonPropertyName("contents")]
    public IReadOnlyList<GeminiContent> Contents { get; set; } = [];

    /// <summary>
    /// A <c>Content</c>, not a string — Anthropic's <c>system</c> is a string and this is not, which
    /// is the sort of near-miss that makes one shared translator a bad idea (64 D1).
    /// </summary>
    [JsonPropertyName("systemInstruction")]
    public GeminiContent? SystemInstruction { get; set; }

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig? GenerationConfig { get; set; }
}

/// <summary>One turn. The two roles Gemini knows are <c>user</c> and <c>model</c>.</summary>
public sealed class GeminiContent
{
    /// <summary>Absent on <c>systemInstruction</c>, where the vendor ignores it.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public IReadOnlyList<GeminiPart> Parts { get; set; } = [];
}

/// <summary>
/// A part is exactly one of its fields. Only <c>text</c> and <c>inlineData</c> are produced here;
/// <c>functionCall</c> and the rest are track-level deferrals.
/// </summary>
public sealed class GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("inlineData")]
    public GeminiInlineData? InlineData { get; set; }

    /// <summary>
    /// True on a thought summary. This dialect never asks for them (<c>includeThoughts</c> is not
    /// sent), and skips them on the way back anyway: it is content the model produced <em>about</em>
    /// the prompt, and rule 7 has no opinion about that yet.
    /// </summary>
    [JsonPropertyName("thought")]
    public bool? Thought { get; set; }
}

/// <summary>
/// Ollama's <c>images</c> arrive as bare base64 and Gemini wants the media type beside them, which
/// is the same answer <see cref="InferHub.Shared.Upstream.Base64MediaType"/> gives Anthropic — a
/// third caller for the sniff phase 29 wrote and phase 63 moved.
/// </summary>
public sealed class GeminiInlineData
{
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

/// <summary>
/// Where every sampling option goes. <b>Gemini has all three of the knobs Anthropic does not</b> —
/// <c>seed</c>, <c>presencePenalty</c> and <c>frequencyPenalty</c> — so 63 D4's drop is a fact about
/// that vendor rather than a house policy, and nothing is dropped here.
/// </summary>
public sealed class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("topP")]
    public double? TopP { get; set; }

    [JsonPropertyName("topK")]
    public int? TopK { get; set; }

    /// <summary>
    /// Sent only when a caller named <c>num_predict</c>. Unlike Anthropic's <c>max_tokens</c> this
    /// is optional to the vendor, so there is no declared default: a ceiling nobody asked for is a
    /// truncated answer nobody can explain (64 D6).
    /// </summary>
    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("stopSequences")]
    public IReadOnlyList<string>? StopSequences { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("presencePenalty")]
    public double? PresencePenalty { get; set; }

    [JsonPropertyName("frequencyPenalty")]
    public double? FrequencyPenalty { get; set; }

    [JsonPropertyName("thinkingConfig")]
    public GeminiThinkingConfig? ThinkingConfig { get; set; }
}

/// <summary>
/// The operator's lever over thinking (64 D6). Absent leaves the vendor's dynamic default, which is
/// on; <c>0</c> disables it on the models that allow that. <c>includeThoughts</c> is deliberately
/// not modelled — asking for thought summaries would put reasoning content on the wire.
/// </summary>
public sealed class GeminiThinkingConfig
{
    [JsonPropertyName("thinkingBudget")]
    public int? ThinkingBudget { get; set; }
}

// ---- responses -----------------------------------------------------------------------

public sealed class GeminiGenerateResponse
{
    /// <summary><b>Empty on a blocked prompt</b>, with the reason under <see cref="PromptFeedback"/> (64 D7).</summary>
    [JsonPropertyName("candidates")]
    public IReadOnlyList<GeminiCandidate>? Candidates { get; set; }

    [JsonPropertyName("promptFeedback")]
    public GeminiPromptFeedback? PromptFeedback { get; set; }

    [JsonPropertyName("usageMetadata")]
    public GeminiUsageMetadata? UsageMetadata { get; set; }

    /// <summary>The model that actually answered, which may be more specific than the one asked for.</summary>
    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("responseId")]
    public string? ResponseId { get; set; }
}

public sealed class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }

    /// <summary><c>STOP</c>, <c>MAX_TOKENS</c>, <c>SAFETY</c>, <c>RECITATION</c>, <c>OTHER</c>, and it grows.</summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }
}

/// <summary>
/// Why the request never reached the model. A response carrying this and no candidates is a 200
/// that looks finished, which is what 64 D7 turns into an error.
/// </summary>
public sealed class GeminiPromptFeedback
{
    /// <summary><c>SAFETY</c>, <c>BLOCKLIST</c>, <c>PROHIBITED_CONTENT</c>, <c>IMAGE_SAFETY</c>, <c>OTHER</c>.</summary>
    [JsonPropertyName("blockReason")]
    public string? BlockReason { get; set; }
}

/// <summary>
/// Five numbers, and the relationships between them are the phase's two counting decisions.
/// <see cref="PromptTokenCount"/> <b>already includes</b> <see cref="CachedContentTokenCount"/> —
/// the opposite of Anthropic, where the cache pair sits beside the input count — so it is passed
/// through whole and nothing is added or subtracted (64 D5). <see cref="ThoughtsTokenCount"/> is
/// billed as output and is still not folded into <c>eval_count</c> (64 D6).
/// </summary>
public sealed class GeminiUsageMetadata
{
    [JsonPropertyName("promptTokenCount")]
    public int? PromptTokenCount { get; set; }

    /// <summary>A breakdown of <see cref="PromptTokenCount"/>, not a sibling of it.</summary>
    [JsonPropertyName("cachedContentTokenCount")]
    public int? CachedContentTokenCount { get; set; }

    [JsonPropertyName("candidatesTokenCount")]
    public int? CandidatesTokenCount { get; set; }

    [JsonPropertyName("thoughtsTokenCount")]
    public int? ThoughtsTokenCount { get; set; }

    [JsonPropertyName("totalTokenCount")]
    public int? TotalTokenCount { get; set; }
}

// ---- embeddings ----------------------------------------------------------------------

/// <summary>
/// <c>:batchEmbedContents</c>, used for one input as well as many (64 D8) — one code path is worth
/// more than a saved field.
/// </summary>
public sealed class GeminiBatchEmbedRequest
{
    [JsonPropertyName("requests")]
    public IReadOnlyList<GeminiEmbedRequest> Requests { get; set; } = [];
}

/// <summary>
/// <b>No <c>taskType</c> is ever set</b> (64 D8). Gemini's own guidance is that
/// <c>RETRIEVAL_DOCUMENT</c> and <c>RETRIEVAL_QUERY</c> produce better vectors than the default, and
/// this hub cannot tell which one it is looking at: Ollama's <c>/api/embed</c> has no such field and
/// phase 44's pipeline calls one model for both ingestion and search.
/// </summary>
public sealed class GeminiEmbedRequest
{
    /// <summary>Required per sub-request, in the <c>models/{id}</c> form the path also uses.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }
}

public sealed class GeminiBatchEmbedResponse
{
    [JsonPropertyName("embeddings")]
    public IReadOnlyList<GeminiEmbedding>? Embeddings { get; set; }
}

public sealed class GeminiEmbedding
{
    [JsonPropertyName("values")]
    public IReadOnlyList<float>? Values { get; set; }
}

// ---- discovery -----------------------------------------------------------------------

/// <summary>One page of <c>GET /v1beta/models</c>. 50 by default and 1000 at most.</summary>
public sealed class GeminiModelPage
{
    [JsonPropertyName("models")]
    public IReadOnlyList<GeminiModelInfo>? Models { get; set; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; set; }
}

public sealed class GeminiModelInfo
{
    /// <summary>
    /// <c>models/gemini-3-pro</c> — the vendor's own form, returned unedited so what a console shows
    /// is pasteable into a <c>ModelMap</c> (64 D2).
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
}
