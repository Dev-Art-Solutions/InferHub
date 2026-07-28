using System.Text.Json;

namespace InferHub.Shared.OpenAi;

/// <summary>
/// Turns one Ollama-shaped chunk into the <em>body</em> of an SSE frame. Deliberately knows nothing
/// about HTTP.
/// </summary>
/// <remarks>
/// <para>
/// Phase 37 moved this out of the coordinator, and the line it was moved along is worth stating:
/// <strong>what turns a result into text is shared; what writes that text to a response is not.</strong>
/// Two hosts formatting the same dialect is exactly how a <c>finish_reason</c> quietly diverges and
/// a client that worked against the hub starts failing against a solo node. The ten lines that call
/// <c>Response.WriteAsync</c> and flush are plumbing, and duplicating those costs nothing — phase 21
/// already settled that hand-writing the framing is right.
/// </para>
/// <para>
/// Every implementation must stay a pure function of the chunk plus its own construction-time state.
/// If one ever needs an <c>HttpContext</c>, it has stopped being a formatter.
/// </para>
/// </remarks>
public interface IOpenAiStreamFormatter
{
    /// <summary>Renders one Ollama chunk as an SSE data payload, or null to skip it.</summary>
    string? FormatChunk(string ollamaJson, bool isFirst);

    /// <summary>
    /// The usage-only frame emitted just before <c>[DONE]</c>. Null unless the caller set
    /// <c>stream_options.include_usage</c>.
    /// </summary>
    string? FormatUsage(string terminalOllamaJson);

    /// <summary>A synthetic terminal frame for a stream that died mid-flight.</summary>
    string FormatTruncation();
}

public sealed class ChatStreamFormatter(
    string id,
    long created,
    string model,
    bool includeUsage) : IOpenAiStreamFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Ollama streams the tool call on a non-terminal chunk, but the terminal chunk must still
    // resolve finish_reason=tool_calls — so once we've seen a call we carry that forward.
    private bool _sawToolCalls;

    public string? FormatChunk(string ollamaJson, bool isFirst)
    {
        var ollama = ResponseTranslator.ParseChat(ollamaJson);
        if (ollama is null)
        {
            return null;
        }

        var chunk = ResponseTranslator.ToChatChunk(ollama, id, created, model, isFirst, _sawToolCalls);

        if (chunk.Choices is [{ Delta.ToolCalls: { Count: > 0 } }, ..])
        {
            _sawToolCalls = true;
        }

        return JsonSerializer.Serialize(chunk, JsonOptions);
    }

    public string? FormatUsage(string terminalOllamaJson)
    {
        if (!includeUsage)
        {
            return null;
        }

        var ollama = ResponseTranslator.ParseChat(terminalOllamaJson);
        var usage = ResponseTranslator.BuildUsage(ollama?.PromptEvalCount, ollama?.EvalCount);
        if (usage is null)
        {
            return null;
        }

        var chunk = ResponseTranslator.ToUsageChunk(usage, id, created, model);
        return JsonSerializer.Serialize(chunk, JsonOptions);
    }

    public string FormatTruncation()
        => JsonSerializer.Serialize(ResponseTranslator.ToTruncationChunk(id, created, model), JsonOptions);
}

public sealed class CompletionStreamFormatter(
    string id,
    long created,
    string model,
    bool includeUsage) : IOpenAiStreamFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? FormatChunk(string ollamaJson, bool isFirst)
    {
        var ollama = ResponseTranslator.ParseGenerate(ollamaJson);
        if (ollama is null)
        {
            return null;
        }

        var chunk = ResponseTranslator.ToCompletionChunk(ollama, id, created, model);
        return JsonSerializer.Serialize(chunk, JsonOptions);
    }

    public string? FormatUsage(string terminalOllamaJson)
    {
        if (!includeUsage)
        {
            return null;
        }

        var ollama = ResponseTranslator.ParseGenerate(terminalOllamaJson);
        var usage = ResponseTranslator.BuildUsage(ollama?.PromptEvalCount, ollama?.EvalCount);
        if (usage is null)
        {
            return null;
        }

        var chunk = new CompletionResponse(id, created, model, [], usage);
        return JsonSerializer.Serialize(chunk, JsonOptions);
    }

    public string FormatTruncation()
    {
        var chunk = new CompletionResponse(
            id,
            created,
            model,
            [new CompletionChoice(0, string.Empty, ResponseTranslator.StopReason)],
            Usage: null);
        return JsonSerializer.Serialize(chunk, JsonOptions);
    }
}

/// <summary>
/// The SSE frame wrappers, so the two hosts cannot disagree about the wire format itself.
/// </summary>
public static class OpenAiSse
{
    public const string ContentType = "text/event-stream";

    public const string DoneFrame = "data: [DONE]\n\n";

    public static string Frame(string json) => $"data: {json}\n\n";
}

/// <summary>
/// The Ollama surface's streaming wire format: one JSON object per line.
/// </summary>
public static class OllamaNdjson
{
    public const string ContentType = "application/x-ndjson";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Line(string json) => json + "\n";

    /// <summary>
    /// The terminal line for a stream that died after the client already holds a 200 and a partial
    /// answer. Closing cleanly with a marked error is the only honest option left; a hung
    /// connection is worse.
    /// </summary>
    public static string ErrorLine(string message)
        => JsonSerializer.Serialize(new { error = message, done = true }, JsonOptions) + "\n";
}
