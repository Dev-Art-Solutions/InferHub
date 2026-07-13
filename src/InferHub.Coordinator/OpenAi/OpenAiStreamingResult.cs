using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// The SSE mirror of <c>StreamingInferenceResult</c>: same channel, same flush-per-chunk
/// discipline, different framing. Written by hand rather than pulled from a package —
/// server-sent events are three lines of string formatting and rule 5 still holds.
/// </summary>
internal sealed class OpenAiStreamingResult(
    ChannelReader<InferenceChunk> chunks,
    IOpenAiStreamFormatter formatter,
    ILogger logger) : IResult
{
    private const string DoneFrame = "data: [DONE]\n\n";

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";

        var isFirst = true;
        string? terminalJson = null;

        try
        {
            await foreach (var chunk in chunks.ReadAllAsync(httpContext.RequestAborted))
            {
                var frame = formatter.FormatChunk(chunk.ResponseJson, isFirst);
                isFirst = false;

                if (frame is not null)
                {
                    await WriteFrameAsync(httpContext, frame);
                }

                if (chunk.Done)
                {
                    terminalJson = chunk.ResponseJson;
                    break;
                }
            }
        }
        catch (NodeDisconnectedException ex)
        {
            // The client already holds a 200 and a partial answer. Closing the stream
            // cleanly is the only honest option left — a hung connection is worse.
            logger.LogWarning(ex, "Node dropped mid-stream; truncating the OpenAI stream with finish_reason=stop");
            await WriteFrameAsync(httpContext, formatter.FormatTruncation());
            await FinishAsync(httpContext);
            return;
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(ex, "Stream timed out; truncating the OpenAI stream with finish_reason=stop");
            await WriteFrameAsync(httpContext, formatter.FormatTruncation());
            await FinishAsync(httpContext);
            return;
        }

        if (terminalJson is not null && formatter.FormatUsage(terminalJson) is { } usageFrame)
        {
            await WriteFrameAsync(httpContext, usageFrame);
        }

        await FinishAsync(httpContext);
    }

    private static async Task WriteFrameAsync(HttpContext httpContext, string json)
    {
        try
        {
            await httpContext.Response.WriteAsync($"data: {json}\n\n", httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            // Client walked away mid-stream.
        }
    }

    private static async Task FinishAsync(HttpContext httpContext)
    {
        try
        {
            await httpContext.Response.WriteAsync(DoneFrame, httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal interface IOpenAiStreamFormatter
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

internal sealed class ChatStreamFormatter(
    string id,
    long created,
    string model,
    bool includeUsage) : IOpenAiStreamFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string? FormatChunk(string ollamaJson, bool isFirst)
    {
        var ollama = ResponseTranslator.ParseChat(ollamaJson);
        if (ollama is null)
        {
            return null;
        }

        var chunk = ResponseTranslator.ToChatChunk(ollama, id, created, model, isFirst);
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

internal sealed class CompletionStreamFormatter(
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
