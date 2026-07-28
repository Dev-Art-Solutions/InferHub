using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;
using InferHub.Shared.OpenAi;
using InferHub.Shared.Vector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// Solo mode's composition point (phase 37): the coordinator's client-facing surface, served by the
/// node itself.
/// </summary>
/// <remarks>
/// <para>
/// The design in one line: <strong>this is the hub's formatting layer sitting directly on the
/// node's executor, with the routing layer removed.</strong> The hub translates a request, admits
/// it, routes it, queues it, dispatches it over SignalR and formats what comes back; solo does the
/// first and the last and skips the middle, because there is nothing to route to. Both ends were
/// already shared code — the translators live in <c>InferHub.Shared/OpenAi/</c> (phase-22 D1) and
/// <see cref="InferenceExecutor"/> already consumes an Ollama-shaped job, which is only true
/// because design rule 6 made the mesh's internal protocol Ollama JSON from the start.
/// </para>
/// <para>
/// <strong>Do not grow routing, admission, queueing or failover here.</strong> If solo mode ever
/// needs one of those it has stopped being solo, and the answer is a coordinator.
/// </para>
/// </remarks>
public static class LocalApiEndpoints
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Set on every response, so a client can tell which shape answered it.</summary>
    internal const string ServedByHeader = "X-InferHub-Served-By";

    internal const string ServedBySolo = "node-solo";

    public static WebApplication MapInferHubLocalApi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<LocalApiOptions>>().Value;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InferHub.Node.LocalApi");

        logger.LogInformation(
            "Solo mode: serving the InferHub client API on {Urls} ({Auth}).",
            options.Urls,
            DescribeAuth(options));

        if (options.AllowAnonymous && !options.BindsLoopbackOnly())
        {
            // Consented to in config, and said out loud on every boot. A dangerous configuration
            // that is silent after the first day is a dangerous configuration nobody remembers.
            logger.LogWarning(
                "{Key} is on and {UrlKey} is not loopback: anyone who can reach {Urls} can spend GPU time on this machine.",
                $"{LocalApiOptions.SectionName}:{nameof(LocalApiOptions.AllowAnonymous)}",
                $"{LocalApiOptions.SectionName}:{nameof(LocalApiOptions.Urls)}",
                options.Urls);
        }

        app.UseMiddleware<LocalApiAuthMiddleware>();

        app.MapLocalStatusEndpoints();
        app.MapLocalInferenceEndpoints();
        app.MapLocalOpenAiEndpoints();

        // Phase 38. Mapped only when there is a corpus to serve, so a node that changes no config
        // keeps the v3.5 surface exactly: these routes 404 and a retrieval header still gets a 501.
        if (app.Services.GetService<RetrievalPipeline>() is not null)
        {
            var retrieval = app.Services.GetRequiredService<IOptions<LocalRetrievalOptions>>().Value;

            logger.LogInformation(
                "Solo retrieval is on: corpus at {DataDirectory}, embedding model {Model}, default mode {Mode}. No coordinator is configured, which is what makes this node the only authority for its collections.",
                Path.GetFullPath(retrieval.DataDirectory),
                retrieval.DefaultEmbeddingModel,
                retrieval.Retrieval.Mode);

            app.MapLocalCollectionEndpoints();
            app.MapLocalIngestionEndpoints();
            app.MapLocalSearchEndpoints();
        }

        return app;
    }

    private static string DescribeAuth(LocalApiOptions options)
    {
        var keys = options.ApiKeys.Count(key => !string.IsNullOrWhiteSpace(key));

        if (options.AllowAnonymous)
        {
            return "no authentication — AllowAnonymous";
        }

        return keys switch
        {
            0 when options.BindsLoopbackOnly() => "loopback only, no keys configured",
            0 => "no keys configured",
            1 => "1 API key",
            _ => $"{keys} API keys"
        };
    }

    // ---- shared plumbing -------------------------------------------------------------------

    internal static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new BadHttpRequestException("request body is required", StatusCodes.Status400BadRequest);
        }

        return body;
    }

    internal static T Deserialize<T>(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(rawJson, JsonOptions)
                ?? throw new BadHttpRequestException("request body is required", StatusCodes.Status400BadRequest);
        }
        catch (JsonException ex)
        {
            throw new BadHttpRequestException($"invalid JSON: {ex.Message}", StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>
    /// A retrieval header against a node whose corpus is switched off is refused rather than
    /// ignored (phase-37 D8, still the behaviour when <c>LocalApi:Retrieval:Enabled</c> is false).
    /// </summary>
    /// <remarks>
    /// Phase-31 D4 settled this shape for a different reason and the reasoning transfers exactly:
    /// answering without the context the caller asked for, silently, is the wrong failure. A
    /// developer who moves a working RAG app onto a solo node and gets confident, fluent,
    /// <em>ungrounded</em> answers files a bug three weeks later that begins "the model got worse".
    /// A 501 is a five-minute fix.
    /// </remarks>
    internal const string RetrievalRefusal =
        "retrieval is not available on this node: it is off. Set LocalApi:Retrieval:Enabled=true (which requires Coordinator:Enabled=false), remove the X-InferHub-Retrieve header, or point this client at a hub.";

    /// <summary>Thrown when a request asks for retrieval and this node has no corpus to answer from.</summary>
    internal sealed class RetrievalNotEnabledException() : InvalidOperationException(RetrievalRefusal);

    /// <summary>
    /// Applies the <c>X-InferHub-Retrieve*</c> headers to an Ollama-shaped request body, returning
    /// the (possibly augmented) body and the serialized <c>X-InferHub-Sources</c> value.
    /// </summary>
    /// <remarks>
    /// Deliberately the same five steps, in the same order, with the same "not augmented means no
    /// sources header" rule as the hub's <c>InferenceEndpoints.ApplyRetrievalAsync</c> — a client
    /// that branches on the presence of the header must not need to know which host answered it.
    /// </remarks>
    internal static async Task<(string RawJson, string? Sources)> ApplyRetrievalAsync(
        HttpContext httpContext,
        bool isChat,
        string rawJson,
        CancellationToken cancellationToken)
    {
        if (!LocalRetrievalHeader.TryRead(httpContext.Request, out var retrieval))
        {
            return (rawJson, Sources: null);
        }

        var pipeline = httpContext.RequestServices.GetService<RetrievalPipeline>()
            ?? throw new RetrievalNotEnabledException();

        var outcome = isChat
            ? await pipeline.AugmentChatAsync(rawJson, Deserialize<ChatRequest>(rawJson), retrieval, cancellationToken)
            : await pipeline.AugmentGenerateAsync(rawJson, Deserialize<GenerateRequest>(rawJson), retrieval, cancellationToken);

        if (!outcome.WasAugmented)
        {
            return (rawJson, Sources: null);
        }

        return (outcome.RawJson, Sources: JsonSerializer.Serialize(outcome.Sources, SourcesJsonOptions));
    }

    /// <summary>
    /// The sources header is serialized with the hub's options, not the endpoint's: a stray
    /// <c>"page":null</c> on every citation from a text file would be a visible difference between
    /// the two hosts for the same corpus.
    /// </summary>
    private static readonly JsonSerializerOptions SourcesJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal static IReadOnlyList<ModelInfo> VisibleModels(
        IReadOnlyList<ModelInfo> models,
        NodeOptions node)
        => ModelFilter.Apply(models, node.Models);

    /// <summary>
    /// Runs the job under the concurrency gate when there is one, rendering the caller's own 503
    /// when the wait expires.
    /// </summary>
    internal static async Task<IResult> WithSlotAsync(
        HttpContext httpContext,
        LocalConcurrencyGate? gate,
        Func<Task<IResult>> run,
        Func<int, IResult> saturated,
        CancellationToken cancellationToken)
    {
        if (gate is null)
        {
            return await run();
        }

        var slot = await gate.TryEnterAsync(cancellationToken);

        if (slot is null)
        {
            httpContext.Response.Headers.RetryAfter = gate.RetryAfterSeconds.ToString();
            return saturated(gate.RetryAfterSeconds);
        }

        try
        {
            var result = await run();

            // A streaming result has not run yet — it holds the slot until its own enumeration
            // ends, so ownership is handed over rather than released here. Releasing now would let
            // the cap be exceeded by exactly the requests that hold the GPU longest.
            if (result is SlotHolding holding)
            {
                holding.Hold(slot);
                return result;
            }

            slot.Dispose();
            return result;
        }
        catch
        {
            slot.Dispose();
            throw;
        }
    }

    /// <summary>A result that keeps the concurrency slot until its stream finishes.</summary>
    internal interface SlotHolding
    {
        void Hold(IDisposable slot);
    }

    /// <summary>Writes an NDJSON stream, framed by <see cref="OllamaNdjson"/>.</summary>
    internal sealed class LocalNdjsonResult(IAsyncEnumerable<InferenceChunk> chunks) : IResult, SlotHolding
    {
        private IDisposable? slot;

        public void Hold(IDisposable held) => slot = held;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                httpContext.Response.ContentType = OllamaNdjson.ContentType;

                await foreach (var chunk in chunks.WithCancellation(httpContext.RequestAborted))
                {
                    await httpContext.Response.WriteAsync(
                        OllamaNdjson.Line(chunk.ResponseJson),
                        httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);

                    if (chunk.Done)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client walked away mid-stream.
            }
            catch (Exception ex)
            {
                // The client already holds a 200 and a partial answer; a marked terminal line is
                // the only honest ending left, and a hung connection is worse.
                await TryWriteErrorLineAsync(httpContext, NodeErrorText.Readable(ex.Message));
            }
            finally
            {
                slot?.Dispose();
            }
        }

        private static async Task TryWriteErrorLineAsync(HttpContext httpContext, string message)
        {
            try
            {
                await httpContext.Response.WriteAsync(OllamaNdjson.ErrorLine(message), httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }
            catch (Exception)
            {
                // Client likely walked away — nothing useful to do here.
            }
        }
    }

    /// <summary>
    /// Writes an SSE stream, framed by <see cref="OpenAiSse"/> with bodies from
    /// <see cref="IOpenAiStreamFormatter"/>.
    /// </summary>
    /// <remarks>
    /// The coordinator has its own ten-line version of this and that duplication is deliberate:
    /// phase-37 D6 shares what turns a result into <em>text</em> and lets each host write it.
    /// </remarks>
    internal sealed class LocalSseResult(
        IAsyncEnumerable<InferenceChunk> chunks,
        IOpenAiStreamFormatter formatter,
        ILogger logger) : IResult, SlotHolding
    {
        private IDisposable? slot;

        public void Hold(IDisposable held) => slot = held;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                httpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                httpContext.Response.ContentType = OpenAiSse.ContentType;
                httpContext.Response.Headers.CacheControl = "no-cache";

                var isFirst = true;
                string? terminalJson = null;

                try
                {
                    await foreach (var chunk in chunks.WithCancellation(httpContext.RequestAborted))
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
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Backend failed mid-stream; truncating the OpenAI stream with finish_reason=stop");
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
            finally
            {
                slot?.Dispose();
            }
        }

        private static async Task WriteFrameAsync(HttpContext httpContext, string json)
        {
            try
            {
                await httpContext.Response.WriteAsync(OpenAiSse.Frame(json), httpContext.RequestAborted);
                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
            }
            catch (OperationCanceledException)
            {
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
}
