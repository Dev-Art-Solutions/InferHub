using System.Text.Json;
using System.Threading.Channels;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Http.Features;

namespace InferHub.Coordinator.OpenAi;

/// <summary>
/// The SSE mirror of <c>StreamingInferenceResult</c>: same channel, same flush-per-chunk
/// discipline, different framing. Written by hand rather than pulled from a package —
/// server-sent events are three lines of string formatting and rule 5 still holds.
/// </summary>
/// <remarks>
/// The <em>frame bodies</em> come from <see cref="IOpenAiStreamFormatter"/> in
/// <c>InferHub.Shared</c> (phase 37) so a solo node cannot format them differently; what stays here
/// is only the writing and flushing. See the remarks on that interface for where the line is.
/// </remarks>
internal sealed class OpenAiStreamingResult(
    ChannelReader<InferenceChunk> chunks,
    IOpenAiStreamFormatter formatter,
    ILogger logger) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        httpContext.Response.ContentType = OpenAiSse.ContentType;
        httpContext.Response.Headers.CacheControl = "no-cache";

        var isFirst = true;
        string? terminalJson = null;

        try
        {
            await foreach (var chunk in chunks.ReadAllAsync(httpContext.RequestAborted))
            {
                // Phase 93: a node's failed-job chunk is a refusal, not a terminal chunk to format
                // as an empty stop. See FailAsync.
                if (chunk.Done && OpenAiSse.TryReadFailure(chunk.ResponseJson, out var failure, out var status))
                {
                    logger.LogWarning("Node failed the streamed job ({Status}); answering the OpenAI client with the error", status);
                    await FailAsync(httpContext, failure, status);
                    return;
                }

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

    /// <summary>
    /// Phase 93, found against a real colibri through a solo node and true here as well: a refused
    /// job reached an OpenAI client as a 200 and an empty <c>finish_reason=stop</c>, which reads as
    /// an empty answer. Before the first byte, the blocking path's own 502; after it, an error frame
    /// the OpenAI SDKs raise on. The node's writer does the same (37 D6).
    /// </summary>
    private static async Task FailAsync(HttpContext httpContext, string message, int status = StatusCodes.Status502BadGateway)
    {
        try
        {
            if (!httpContext.Response.HasStarted)
            {
                // Phase 100: a node's refusal keeps its 4xx (a picture for a model that cannot read one).
                httpContext.Response.StatusCode = status;
                await httpContext.Response.WriteAsJsonAsync(
                    OpenAiErrorEnvelope.Create(message, status < 500 ? OpenAiErrorTypes.InvalidRequest : OpenAiErrorTypes.ApiError),
                    ErrorJsonOptions,
                    httpContext.RequestAborted);
                return;
            }

            await httpContext.Response.WriteAsync(OpenAiSse.ErrorFrame(message), httpContext.RequestAborted);
            await httpContext.Response.WriteAsync(OpenAiSse.DoneFrame, httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task WriteFrameAsync(HttpContext httpContext, string json)
    {
        try
        {
            await httpContext.Response.WriteAsync(OpenAiSse.Frame(json), httpContext.RequestAborted);
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
            await httpContext.Response.WriteAsync(OpenAiSse.DoneFrame, httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
