using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>POST /api/tools/{capability}</c>, served by the node itself (phase 41, D8).
/// </summary>
/// <remarks>
/// Solo mode gets tools on the same day the mesh does, because it is the same executor with routing
/// deleted — phase-37 D2's framing a third time. A solo bundled node that transcribes with one
/// <c>docker run</c> is where this track is heading, and splitting the local path across releases
/// would mean building it twice.
/// </remarks>
internal static class LocalToolEndpoints
{
    public static IEndpointRouteBuilder MapLocalToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tools/{capability}", HandleAsync);
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        string capability,
        ToolExecutor executor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return Error(StatusCodes.Status400BadRequest, "a capability is required, e.g. /api/tools/transcribe");
        }

        // The same subtractive key the mesh honours, enforced here because there is no router to
        // honour it (phase-40 D5). One key must not mean two things.
        if (LocalApiEndpoints.CapabilityDisabled(httpContext, capability, out var refusal))
        {
            return Error(StatusCodes.Status503ServiceUnavailable, refusal);
        }

        LocalToolRequest body;

        try
        {
            body = await LocalToolRequest.ReadAsync(httpContext, cancellationToken);
        }
        catch (LocalToolRequest.TooLargeException ex)
        {
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

        if (!executor.Provides(capability, body.Model))
        {
            // The hub's 503 with the same Retry-After, in the node's own words: "this node" rather
            // than "no node", because on a solo box those are the same sentence and only one of
            // them is true.
            httpContext.Response.Headers.RetryAfter = ToolExecutor.CapabilityRetryAfterSeconds.ToString();

            return Error(
                StatusCodes.Status503ServiceUnavailable,
                $"this node does not provide '{capability}' for model '{body.Model}'");
        }

        var job = new ToolJob(Guid.NewGuid(), capability, body.Model, body.Payload, body.Attachments);
        httpContext.Response.Headers[LocalApiEndpoints.ServedByHeader] = LocalApiEndpoints.ServedBySolo;

        if (body.Stream)
        {
            return new LocalToolNdjsonResult(executor.StreamAsync(job, cancellationToken));
        }

        var result = await executor.RunAsync(job, cancellationToken);
        return Render(httpContext, result);
    }

    /// <summary>
    /// The hub's <c>ToolEndpoints.Render</c>, hand-copied. What a client sees must not depend on
    /// which host answered, and the ten lines that write it are per host (phase-37 D6) — so this is
    /// the phase's parity risk, and <c>SoloToolParityTests</c> is what keeps it honest.
    /// </summary>
    private static IResult Render(HttpContext httpContext, ToolResult result)
    {
        if (!result.Success)
        {
            if (result.RetryAfterSeconds is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter = retryAfter.ToString();
                return Error(StatusCodes.Status503ServiceUnavailable, result.Error ?? "the tool is busy");
            }

            if (ToolErrorCodes.IsClientError(result.ErrorCode))
            {
                return Error(StatusCodes.Status400BadRequest, NodeErrorText.Readable(result.Error));
            }

            return Error(StatusCodes.Status502BadGateway, NodeErrorText.Readable(result.Error));
        }

        if (result.Attachments is { Count: > 0 } attachments)
        {
            if (attachments.Count > 1)
            {
                return Error(
                    StatusCodes.Status501NotImplemented,
                    $"the tool returned {attachments.Count} files, and /api/tools/{{capability}} can only return one. Use a capability-specific endpoint.");
            }

            var only = attachments[0];
            return Results.Bytes(only.Bytes, only.MediaType, only.Name);
        }

        return Results.Text(result.Payload ?? "{}", "application/json");
    }

    private static IResult Error(int statusCode, string message)
        => Results.Json(new { error = message }, LocalApiEndpoints.JsonOptions, statusCode: statusCode);

    private sealed class LocalToolNdjsonResult(IAsyncEnumerable<ToolChunk> chunks) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            httpContext.Response.ContentType = "application/x-ndjson";

            try
            {
                await foreach (var chunk in chunks.WithCancellation(httpContext.RequestAborted))
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
        }
    }
}

/// <summary>
/// The node's copy of the hub's <c>ToolRequestBody</c>. Duplicated on purpose: design rule 2 keeps
/// ASP.NET out of <c>InferHub.Shared</c>, so the form reading is per host while the cap and its
/// sentence are shared in <see cref="ToolAttachmentLimits"/>.
/// </summary>
internal sealed record LocalToolRequest(
    string? Model,
    string Payload,
    bool Stream,
    IReadOnlyList<ToolAttachment>? Attachments)
{
    public static async Task<LocalToolRequest> ReadAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var maxBytes = httpContext.RequestServices.GetService<IOptions<ToolOptions>>()?.Value.MaxAttachmentBytes
            ?? ToolAttachmentLimits.DefaultMaxBytes;

        return httpContext.Request.HasFormContentType
            ? await ReadMultipartAsync(httpContext, maxBytes, cancellationToken)
            : await ReadJsonAsync(httpContext, cancellationToken);
    }

    private static async Task<LocalToolRequest> ReadJsonAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var raw = await LocalApiEndpoints.ReadBodyAsync(httpContext.Request, cancellationToken);

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException ex)
        {
            throw new BadHttpRequestException($"invalid JSON: {ex.Message}", StatusCodes.Status400BadRequest);
        }

        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new BadHttpRequestException("the request body must be a JSON object", StatusCodes.Status400BadRequest);
        }

        var model = root.TryGetProperty("model", out var m) && m.ValueKind is JsonValueKind.String
            ? m.GetString()
            : null;

        var stream = root.TryGetProperty("stream", out var s)
            ? s.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => StreamFromQuery(httpContext)
            }
            : StreamFromQuery(httpContext);

        return new LocalToolRequest(model, raw, stream, null);
    }

    private static async Task<LocalToolRequest> ReadMultipartAsync(
        HttpContext httpContext,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var model = form["model"].FirstOrDefault();
        var payload = form["payload"].FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw new BadHttpRequestException(
                    $"the 'payload' field is not valid JSON: {ex.Message}",
                    StatusCodes.Status400BadRequest);
            }
        }
        else
        {
            var fields = form
                .Where(field => field.Key is not ("model" or "payload" or "stream"))
                .ToDictionary(field => field.Key, field => (object?)field.Value.FirstOrDefault());

            fields["model"] = model;
            payload = JsonSerializer.Serialize(fields, LocalApiEndpoints.JsonOptions);
        }

        var attachments = new List<ToolAttachment>();

        foreach (var file in form.Files)
        {
            if (file.Length > maxBytes)
            {
                throw new TooLargeException(
                    ToolAttachmentLimits.TooLarge(file.FileName ?? file.Name, file.Length, maxBytes));
            }

            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, cancellationToken);
            attachments.Add(new ToolAttachment(
                file.Name,
                string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                buffer.ToArray()));
        }

        var stream = bool.TryParse(form["stream"].FirstOrDefault(), out var parsed)
            ? parsed
            : StreamFromQuery(httpContext);

        return new LocalToolRequest(model, payload!, stream, attachments.Count == 0 ? null : attachments);
    }

    private static bool StreamFromQuery(HttpContext httpContext) =>
        bool.TryParse(httpContext.Request.Query["stream"].FirstOrDefault(), out var fromQuery) && fromQuery;

    internal sealed class TooLargeException(string message) : InvalidOperationException(message);
}
