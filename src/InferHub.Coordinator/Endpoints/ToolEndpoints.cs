using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// The generic client surface for tools (phase 41): <c>POST /api/tools/{capability}</c>.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately <em>generic</em>. The dialect-shaped routes — <c>/v1/audio/transcriptions</c>
/// and <c>/v1/audio/speech</c> — land in phase 42 with a real worker behind them, and they will sit
/// <b>beside</b> this one rather than replacing it: an operator who writes their own tool needs a
/// way to call it that InferHub did not have to know about in advance.
/// </para>
/// <para>
/// It sits under <c>/api</c>, which <c>BearerApiKeyMiddleware</c> already guards. That is checked
/// rather than assumed: phase-21 D2's failure mode is adding a client-facing route under a prefix
/// nobody guards and shipping an unauthenticated inference API, and <c>ToolEndpointTests</c> fails
/// if this route ever answers without a key.
/// </para>
/// </remarks>
public static class ToolEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tools/{capability}", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        string capability,
        INodeRegistry registry,
        Services.IRouter router,
        IToolDispatcher tools,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("InferHub.Coordinator.Tools");

        if (string.IsNullOrWhiteSpace(capability))
        {
            return Error(StatusCodes.Status400BadRequest, "a capability is required, e.g. /api/tools/transcribe");
        }

        ToolRequestBody body;

        try
        {
            body = await ToolRequestBody.ReadAsync(httpContext, cancellationToken);
        }
        catch (ToolRequestTooLargeException ex)
        {
            // Refused at the edge, before anything is buffered onward, with the limit in the
            // message so the caller does not have to find it by bisection (phase-40 D4).
            return Error(StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            return Error(StatusCodes.Status400BadRequest, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(body.Model))
        {
            return Error(StatusCodes.Status400BadRequest, "model is required");
        }

        var node = router.Route(body.Model, capability: capability);

        if (node is null)
        {
            // Phase-40 D5, unchanged: a capability nobody provides is fleet state and gets the
            // saturation shape; a model nobody holds at all is still the 404.
            //
            // "Holds it" has to be asked of the capability declarations, not only of the backend
            // model list, because a tool's models are not Ollama models — a box that only
            // transcribes reports zero models and declares `transcribe: [whisper-small]`, and
            // asking the model list alone would call every one of its models non-existent.
            if (KnownToTheFleet(registry, body.Model))
            {
                httpContext.Response.Headers.RetryAfter = CapabilityRetryAfterSeconds.ToString();

                return Error(
                    StatusCodes.Status503ServiceUnavailable,
                    $"no node currently provides '{capability}' for model '{body.Model}'");
            }

            return Error(StatusCodes.Status404NotFound, $"model '{body.Model}' not found");
        }

        var job = new ToolJob(Guid.NewGuid(), capability, body.Model, body.Payload, body.Attachments);
        httpContext.Response.Headers["X-InferHub-Served-By"] = "node";

        if (body.Stream)
        {
            var reader = await tools.DispatchToolStreamAsync(node, job, cancellationToken);
            return new ToolNdjsonResult(reader);
        }

        var result = await tools.DispatchToolAsync(node, job, cancellationToken);

        logger.LogInformation(
            "Tool job {JobId} ({Capability}/{Model}) on node {NodeId}: success={Success}",
            job.JobId,
            capability,
            body.Model,
            node.NodeId,
            result.Success);

        return Render(httpContext, result);
    }

    /// <summary>Matches phase-40 D5's hint, so a client's backoff is the same for every refusal.</summary>
    internal const int CapabilityRetryAfterSeconds = 30;

    /// <summary>Does any node offer this model, for any kind of work?</summary>
    internal static bool KnownToTheFleet(INodeRegistry registry, string model) =>
        registry.FindNodesWithModel(model).Count > 0
        || registry.CapabilitySummary()
            .Any(summary => summary.Models.Contains(model, StringComparer.OrdinalIgnoreCase));

    internal static IResult Render(HttpContext httpContext, ToolResult result)
    {
        if (!result.Success)
        {
            if (result.RetryAfterSeconds is { } retryAfter)
            {
                // The node stated a fact — every worker busy, or the tool temporarily not running —
                // and the edge renders it. Nothing here reads the error *text* to decide the
                // status; that is the inference phase-29 D6 refuses to make.
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
                return Error(StatusCodes.Status503ServiceUnavailable, result.Error ?? "the tool is busy");
            }

            if (ToolErrorCodes.IsClientError(result.ErrorCode))
            {
                // The worker named the request as the problem. Its message says what it can do.
                return Error(StatusCodes.Status400BadRequest, NodeErrorText.Readable(result.Error));
            }

            return Error(StatusCodes.Status502BadGateway, NodeErrorText.Readable(result.Error));
        }

        if (result.Attachments is { Count: > 0 } attachments)
        {
            if (attachments.Count > 1)
            {
                // Refused rather than truncated. A generic route has no way to frame several files
                // that every client would agree on, and returning the first while dropping the rest
                // is a lie with a 200 on it. A dialect route (phase 42) decides its own shape.
                return Error(
                    StatusCodes.Status501NotImplemented,
                    $"the tool returned {attachments.Count} files, and /api/tools/{{capability}} can only return one. Use a capability-specific endpoint.");
            }

            var only = attachments[0];
            return Results.Bytes(only.Bytes, only.MediaType, only.Name);
        }

        return Results.Text(result.Payload ?? "{}", "application/json");
    }

    internal static IResult Error(int statusCode, string message)
        => Results.Json(new { error = message }, JsonOptions, statusCode: statusCode);

    /// <summary>Streams tool chunks as NDJSON, the framing the Ollama surface already uses.</summary>
    internal sealed class ToolNdjsonResult(System.Threading.Channels.ChannelReader<ToolChunk> reader) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            httpContext.Response.ContentType = "application/x-ndjson";

            try
            {
                await foreach (var chunk in reader.ReadAllAsync(httpContext.RequestAborted))
                {
                    await httpContext.Response.WriteAsync(chunk.Payload + "\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

                    if (chunk.Done)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The client holds a 200 and a partial answer; a marked terminal line is the only
                // honest ending left, and a hung connection is worse.
                try
                {
                    var line = JsonSerializer.Serialize(
                        new { error = NodeErrorText.Readable(ex.Message), done = true },
                        JsonOptions);

                    await httpContext.Response.WriteAsync(line + "\n", httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
