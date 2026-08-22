using System.Text.Json;
using System.Text.Json.Serialization;

namespace InferHub.Shared.Anthropic;

/// <summary>
/// Anthropic's <c>POST /v1/messages</c> body (phase 63). Every field here was read from the
/// vendor's current documentation on the day the phase was written, not recalled — 57's precedent,
/// which is in this repository because two of that phase's recalled facts were wrong.
/// </summary>
/// <remarks>
/// <b><c>max_tokens</c> is required</b> and Ollama has no equivalent, which is why
/// <c>Providers:&lt;id&gt;:MaxTokens</c> exists (63 D2). <c>system</c> is a top-level field rather
/// than a role, which is why 63 D3 lifts every system message into it.
/// </remarks>
public sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<AnthropicMessage> Messages { get; set; } = [];

    [JsonPropertyName("system")]
    public string? System { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

    [JsonPropertyName("stop_sequences")]
    public IReadOnlyList<string>? StopSequences { get; set; }
}

/// <summary>One turn. The two roles Anthropic knows are <c>user</c> and <c>assistant</c>.</summary>
public sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>A plain string for text, or an array of content blocks when an image rides along.</summary>
    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
}

/// <summary>A block of a response's <c>content</c> array. Only <c>text</c> is translated (63 §1).</summary>
public sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class AnthropicMessageResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    public IReadOnlyList<AnthropicContentBlock>? Content { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary><c>end_turn</c>, <c>max_tokens</c>, <c>stop_sequence</c>, <c>tool_use</c>, and more over time.</summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>
/// Four numbers, not two. The cache pair is reported separately and priced separately, so 63 D5
/// does <b>not</b> fold it into <c>prompt_eval_count</c>: a total this hub invented would match no
/// line on the invoice.
/// </summary>
public sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; set; }
}

/// <summary>
/// One SSE frame's payload. The named event and the payload's own <c>type</c> always agree, so the
/// reader keys on the payload and never has to hold the <c>event:</c> line.
/// </summary>
public sealed class AnthropicStreamEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>On <c>message_start</c> only: the whole message, with empty content and the input count.</summary>
    [JsonPropertyName("message")]
    public AnthropicMessageResponse? Message { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicStreamDelta? Delta { get; set; }

    /// <summary>On <c>message_delta</c>: the counts so far. <b>Cumulative</b> — see 63 D5.</summary>
    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }

    [JsonPropertyName("error")]
    public AnthropicErrorBody? Error { get; set; }
}

public sealed class AnthropicStreamDelta
{
    /// <summary><c>text_delta</c> is the one this dialect forwards; <c>input_json_delta</c> and the thinking ones are not.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }
}

/// <summary>One page of <c>GET /v1/models</c>. Paginated, 20 by default and 1000 at most.</summary>
public sealed class AnthropicModelPage
{
    [JsonPropertyName("data")]
    public IReadOnlyList<AnthropicModelInfo>? Data { get; set; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("first_id")]
    public string? FirstId { get; set; }

    [JsonPropertyName("last_id")]
    public string? LastId { get; set; }
}

public sealed class AnthropicModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}
