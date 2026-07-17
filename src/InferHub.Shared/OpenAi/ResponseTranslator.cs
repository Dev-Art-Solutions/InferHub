using System.Buffers.Binary;
using System.Text.Json;
using InferHub.Shared.Ollama;

namespace InferHub.Shared.OpenAi;

/// <summary>
/// Ollama response → OpenAI response. The completion id is minted once per request and
/// reused across every chunk of a stream, which is what clients key their reassembly on.
/// </summary>
public static class ResponseTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const string StopReason = "stop";
    public const string LengthReason = "length";
    public const string ToolCallsReason = "tool_calls";

    public static string NewCompletionId() => "chatcmpl-" + Guid.NewGuid().ToString("N");

    public static string NewLegacyCompletionId() => "cmpl-" + Guid.NewGuid().ToString("N");

    public static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static ChatResponse? ParseChat(string ollamaJson)
        => JsonSerializer.Deserialize<ChatResponse>(ollamaJson, JsonOptions);

    public static GenerateResponse? ParseGenerate(string ollamaJson)
        => JsonSerializer.Deserialize<GenerateResponse>(ollamaJson, JsonOptions);

    public static ChatCompletionResponse ToChatCompletion(
        ChatResponse ollama,
        string id,
        long created,
        string requestedModel)
    {
        var toolCalls = ExtractToolCalls(ollama.Message);

        var message = new ChatCompletionResponseMessage(
            Role: ollama.Message?.Role ?? "assistant",
            Content: ollama.Message?.Content ?? string.Empty,
            ToolCalls: toolCalls);

        var choice = new ChatCompletionChoice(
            Index: 0,
            Message: message,
            FinishReason: ResolveFinishReason(ollama.AdditionalProperties, toolCalls is { Count: > 0 }));

        return new ChatCompletionResponse(
            id,
            created,
            ollama.Model ?? requestedModel,
            [choice],
            BuildUsage(ollama.PromptEvalCount, ollama.EvalCount));
    }

    public static ChatCompletionChunk ToChatChunk(
        ChatResponse ollama,
        string id,
        long created,
        string requestedModel,
        bool isFirst,
        bool toolCallsAlreadySeen = false)
    {
        var done = ollama.Done == true;

        // The whole tool call arrives in one chunk — we do not fabricate a stream of argument
        // fragments Ollama never sent (rule 6 / phase 27 note). Which chunk it lands on varies
        // by Ollama version: observed on a non-terminal chunk (done:false), while the phase
        // brief assumed the terminal one. Either way we emit it as it arrives; the terminal
        // chunk still resolves finish_reason=tool_calls via <paramref name="toolCallsAlreadySeen"/>.
        var toolCalls = ExtractStreamingToolCalls(ollama.Message);

        // Only the opening chunk announces the role; a tool-call delta carries the calls with
        // null content (a tool-call frame and a text frame are not the same frame); the plain
        // terminal chunk carries an empty delta and the finish_reason.
        ChatCompletionDelta delta;
        if (toolCalls is { Count: > 0 })
        {
            delta = new ChatCompletionDelta(
                Role: isFirst ? "assistant" : null,
                Content: null,
                ToolCalls: toolCalls);
        }
        else if (done)
        {
            delta = new ChatCompletionDelta(Role: null, Content: null);
        }
        else
        {
            delta = new ChatCompletionDelta(
                Role: isFirst ? "assistant" : null,
                Content: ollama.Message?.Content ?? string.Empty);
        }

        var finishReason = done
            ? ResolveFinishReason(
                ollama.AdditionalProperties,
                toolCalls is { Count: > 0 } || toolCallsAlreadySeen)
            : null;

        var choice = new ChatCompletionChunkChoice(Index: 0, Delta: delta, FinishReason: finishReason);

        return new ChatCompletionChunk(id, created, ollama.Model ?? requestedModel, [choice], Usage: null);
    }

    /// <summary>
    /// The usage-only chunk emitted before <c>[DONE]</c> when the caller asked for
    /// <c>stream_options.include_usage</c>. <c>choices</c> is empty by contract.
    /// </summary>
    public static ChatCompletionChunk ToUsageChunk(
        OpenAiUsage usage,
        string id,
        long created,
        string model)
        => new(id, created, model, [], usage);

    /// <summary>
    /// A synthetic terminal chunk. Used when the node dies mid-stream: the client has
    /// already been handed a 200 and a partial answer, so the only honest thing left is to
    /// close the stream cleanly rather than let it hang.
    /// </summary>
    public static ChatCompletionChunk ToTruncationChunk(string id, long created, string model)
        => new(
            id,
            created,
            model,
            [new ChatCompletionChunkChoice(0, new ChatCompletionDelta(null, null), StopReason)],
            Usage: null);

    public static CompletionResponse ToCompletion(
        GenerateResponse ollama,
        string id,
        long created,
        string requestedModel)
    {
        var choice = new CompletionChoice(
            Index: 0,
            Text: ollama.Response ?? string.Empty,
            FinishReason: ResolveFinishReason(ollama.AdditionalProperties, hasToolCalls: false));

        return new CompletionResponse(
            id,
            created,
            ollama.Model ?? requestedModel,
            [choice],
            BuildUsage(ollama.PromptEvalCount, ollama.EvalCount));
    }

    public static CompletionResponse ToCompletionChunk(
        GenerateResponse ollama,
        string id,
        long created,
        string requestedModel)
    {
        var done = ollama.Done == true;

        var choice = new CompletionChoice(
            Index: 0,
            Text: ollama.Response ?? string.Empty,
            FinishReason: done ? ResolveFinishReason(ollama.AdditionalProperties, hasToolCalls: false) : null);

        return new CompletionResponse(id, created, ollama.Model ?? requestedModel, [choice], Usage: null);
    }

    public static OpenAiUsage? BuildUsage(int? promptEvalCount, int? evalCount)
    {
        // Reporting zeros would be worse than reporting nothing: clients bill and budget off
        // these numbers.
        if (promptEvalCount is null && evalCount is null)
        {
            return null;
        }

        var prompt = promptEvalCount ?? 0;
        var completion = evalCount ?? 0;
        return new OpenAiUsage(prompt, completion, prompt + completion);
    }

    internal static string ResolveFinishReason(
        Dictionary<string, JsonElement>? additionalProperties,
        bool hasToolCalls)
    {
        if (hasToolCalls)
        {
            return ToolCallsReason;
        }

        if (additionalProperties is not null
            && additionalProperties.TryGetValue("done_reason", out var reason)
            && reason.ValueKind == JsonValueKind.String
            && string.Equals(reason.GetString(), LengthReason, StringComparison.OrdinalIgnoreCase))
        {
            return LengthReason;
        }

        return StopReason;
    }

    /// <summary>
    /// The streaming shape of <see cref="ExtractToolCalls"/>: the same calls (id, name,
    /// stringified arguments) with a 0-based <c>index</c> in array order, as the streaming
    /// spec requires. Reusing the blocking extractor keeps the streamed call byte-equivalent
    /// (id aside) to the blocking <see cref="ToChatCompletion"/> result for the same body.
    /// </summary>
    internal static IReadOnlyList<StreamingToolCall>? ExtractStreamingToolCalls(ChatMessage? message)
    {
        if (ExtractToolCalls(message) is not { Count: > 0 } calls)
        {
            return null;
        }

        var streaming = new List<StreamingToolCall>(calls.Count);
        for (var i = 0; i < calls.Count; i++)
        {
            streaming.Add(new StreamingToolCall(i, calls[i].Id, calls[i].Function));
        }

        return streaming;
    }

    /// <summary>
    /// Ollama nests tool calls under <c>message.tool_calls[].function</c> with the arguments
    /// as an object. OpenAI wants a synthesized call id, an explicit type, and the arguments
    /// serialized to a string.
    /// </summary>
    internal static IReadOnlyList<OpenAiToolCall>? ExtractToolCalls(ChatMessage? message)
    {
        if (message?.AdditionalProperties is not { } extras
            || !extras.TryGetValue("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array
            || toolCalls.GetArrayLength() == 0)
        {
            return null;
        }

        var calls = new List<OpenAiToolCall>();

        foreach (var call in toolCalls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object
                || !call.TryGetProperty("function", out var function)
                || function.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            var arguments = function.TryGetProperty("arguments", out var argsElement)
                ? StringifyArguments(argsElement)
                : "{}";

            // Ollama does not mint call ids; the id only has to be stable within the
            // response so the client can pair a tool result back to its call.
            var id = call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()!
                : "call_" + Guid.NewGuid().ToString("N")[..24];

            calls.Add(new OpenAiToolCall(id, new OpenAiToolCallFunction(name, arguments)));
        }

        return calls.Count == 0 ? null : calls;
    }

    private static string StringifyArguments(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.String
            ? arguments.GetString() ?? "{}"
            : arguments.GetRawText();

    /// <summary>
    /// base64 of the little-endian float32 array — the encoding the OpenAI Python SDK asks
    /// for by default and then decodes with numpy.
    /// </summary>
    public static string ToBase64(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count * sizeof(float)];

        for (var i = 0; i < vector.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    public static OpenAiEmbeddingsResponse ToEmbeddings(
        EmbedResponse ollama,
        string requestedModel,
        bool base64)
    {
        var data = new List<OpenAiEmbedding>(ollama.Embeddings.Count);

        for (var i = 0; i < ollama.Embeddings.Count; i++)
        {
            var vector = ollama.Embeddings[i];
            object payload = base64 ? ToBase64(vector) : vector.ToArray();
            data.Add(new OpenAiEmbedding(i, payload));
        }

        var promptTokens = ollama.PromptEvalCount ?? 0;

        return new OpenAiEmbeddingsResponse(
            ollama.Model ?? requestedModel,
            data,
            new OpenAiEmbeddingsUsage(promptTokens, promptTokens));
    }
}
