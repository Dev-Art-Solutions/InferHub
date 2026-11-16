using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ollama;

namespace InferHub.Coordinator.Endpoints;

/// <summary>
/// Phase 150: <c>/api/jobs</c> — a chat or a generate submitted as a job. Client-scoped like every route under the
/// bearer guard: a job is found only by the client that submitted it, and somebody else's id is the same 404 as one
/// that never existed (25 D4). <c>/api/admin/jobs</c> lists every job for an operator, without a request or an answer.
/// </summary>
public static class JobEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/jobs", SubmitAsync);
        app.MapGet("/api/jobs", (HttpContext http, TextJobRegistry jobs) => Off(jobs) ?? Results.Json(
            new { jobs = jobs.ForClient(ClientOf(http)).Select(j => Describe(j, jobs)).ToArray() }, JsonOptions));
        app.MapGet("/api/jobs/{id:guid}", (Guid id, HttpContext http, TextJobRegistry jobs) =>
            Off(jobs) ?? (jobs.Find(id, ClientOf(http)) is { } job ? Results.Json(Describe(job, jobs), JsonOptions) : NotFound(id)));
        app.MapGet("/api/jobs/{id:guid}/result", Result);
        app.MapGet("/api/jobs/{id:guid}/events", EventsAsync);
        app.MapDelete("/api/jobs/{id:guid}", Delete);

        app.MapGet("/api/admin/jobs", (TextJobRegistry jobs) =>
        {
            var (queued, running, finished) = jobs.Counts();

            return Results.Json(new
            {
                enabled = jobs.Enabled,
                queued,
                running,
                finished,
                retentionSeconds = jobs.Options.RetentionSeconds,
                jobs = jobs.All().Select(j => Describe(j, jobs, withClient: true)).ToArray()
            }, JsonOptions);
        });

        return app;
    }

    private static string ClientOf(HttpContext http) => BearerApiKeyMiddleware.ClientOf(http).Id;

    private static IResult? Off(TextJobRegistry jobs) => jobs.Enabled
        ? null
        : Error(StatusCodes.Status404NotFound, $"jobs are off on this hub ({JobsOptions.SectionName}:{nameof(JobsOptions.Enabled)})");

    private static IResult NotFound(Guid id) => Error(StatusCodes.Status404NotFound, $"job '{id}' not found");

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, JsonOptions, statusCode: status);

    private static async Task<IResult> SubmitAsync(HttpContext http, TextJobRegistry jobs, CancellationToken cancellationToken)
    {
        if (Off(jobs) is { } off)
        {
            return off;
        }

        // Retrieval is applied by the blocking routes while the caller waits; a job that quietly answered without
        // the context its submitter asked for would be 37 D8's failure, so it is refused instead.
        if (http.Request.Headers.ContainsKey(InferenceEndpoints.RetrieveHeader))
        {
            return Error(StatusCodes.Status400BadRequest,
                $"{InferenceEndpoints.RetrieveHeader} is not applied to a job; send the request to /api/chat or /api/generate");
        }

        JsonObject? body;

        try
        {
            body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: cancellationToken) as JsonObject;
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body?["request"] is not JsonObject request)
        {
            return Error(StatusCodes.Status400BadRequest,
                "the body is {\"kind\": \"chat\" | \"generate\", \"request\": { ... }} — request is the body /api/chat or /api/generate takes");
        }

        var kind = (body["kind"] as JsonValue)?.TryGetValue<string>(out var named) == true
            ? named.Trim().ToLowerInvariant()
            : request["messages"] is not null ? "chat" : "generate";

        if (kind is not ("chat" or "generate"))
        {
            return Error(StatusCodes.Status400BadRequest, $"kind '{kind}' is not a job; a job is 'chat' or 'generate'");
        }

        var model = (request["model"] as JsonValue)?.TryGetValue<string>(out var name) == true ? name : null;

        if (string.IsNullOrWhiteSpace(model))
        {
            return Error(StatusCodes.Status400BadRequest, "request.model is required");
        }

        // A job is always read piece by piece: that is what its progress is counted from.
        request["stream"] = true;
        var rawJson = request.ToJsonString();

        string? conversation = null;

        if (kind == "chat")
        {
            try
            {
                conversation = InferenceEndpoints.ResolveConversationKey(
                    http.Request, JsonSerializer.Deserialize<ChatRequest>(rawJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            }
            catch (JsonException ex)
            {
                return Error(StatusCodes.Status400BadRequest, $"request is not a chat request: {ex.Message}");
            }
        }

        // The submit's line on the audit trail names the model; the node is not on it (125 D2 — it is chosen later).
        AuditScope.Asked(model, CapabilityKinds.ForJobKind(kind));

        var (job, refusal) = jobs.Submit(
            InferenceCore.ClientContext.From(http), kind, rawJson, model, conversation, ProviderSteer.From(http.Request));

        if (refusal is not null)
        {
            if (refusal.RetryAfterSeconds is { } retry)
            {
                http.Response.Headers.RetryAfter = retry.ToString();
            }

            return Error(refusal.Status, refusal.Message);
        }

        http.Response.Headers.Location = $"/api/jobs/{job!.Id}";
        return Results.Json(Describe(job, jobs), JsonOptions, statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Result(Guid id, HttpContext http, TextJobRegistry jobs)
    {
        if (Off(jobs) is { } off)
        {
            return off;
        }

        if (jobs.Find(id, ClientOf(http)) is not { } job)
        {
            return NotFound(id);
        }

        switch (job.Status)
        {
            case TextJobStatus.Succeeded:
                http.Response.Headers[InferenceCore.ServedByHeader] = job.ServedBy ?? InferenceCore.ServedByNode;
                return Results.Text(job.ResultJson(), "application/json");

            case TextJobStatus.Failed:
                if (job.RetryAfterSeconds is { } retry)
                {
                    http.Response.Headers.RetryAfter = retry.ToString();
                }

                return Error(job.ErrorStatus ?? StatusCodes.Status502BadGateway, job.ErrorMessage ?? "the job failed");

            case TextJobStatus.Cancelled:
                return Error(StatusCodes.Status409Conflict, "the job was cancelled");

            default:
                // What has arrived so far, done: false — so a caller who reconnects reads the answer up to now.
                return Results.Text(job.ResultJson(), "application/json", statusCode: StatusCodes.Status202Accepted);
        }
    }

    private static IResult Delete(Guid id, HttpContext http, TextJobRegistry jobs)
    {
        if (Off(jobs) is { } off)
        {
            return off;
        }

        if (jobs.Find(id, ClientOf(http)) is not { } job)
        {
            return NotFound(id);
        }

        if (job.IsTerminal)
        {
            // Finished: the caller is done with it, and the answer goes now rather than at the retention window.
            jobs.Remove(job);
            return Results.NoContent();
        }

        jobs.Cancel(job);
        return Results.Json(Describe(job, jobs), JsonOptions, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>Server-sent events: the job's status each time it changes (twice a second at most), ending with the terminal one.</summary>
    private static async Task EventsAsync(Guid id, HttpContext http, TextJobRegistry jobs, CancellationToken cancellationToken)
    {
        if (Off(jobs) is { } off)
        {
            await off.ExecuteAsync(http);
            return;
        }

        if (jobs.Find(id, ClientOf(http)) is not { } job)
        {
            await NotFound(id).ExecuteAsync(http);
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var seen = -1;

        try
        {
            while (true)
            {
                var changed = job.Changed;

                if (job.Version != seen)
                {
                    seen = job.Version;
                    var terminal = job.IsTerminal;
                    await http.Response.WriteAsync(
                        $"event: status\ndata: {JsonSerializer.Serialize(Describe(job, jobs), JsonOptions)}\n\n", cancellationToken);
                    await http.Response.Body.FlushAsync(cancellationToken);

                    if (terminal)
                    {
                        return;
                    }

                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                if (await Task.WhenAny(changed, Task.Delay(TimeSpan.FromSeconds(15), cancellationToken)) != changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await http.Response.WriteAsync(": waiting\n\n", cancellationToken);
                    await http.Response.Body.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A job's status: names, counts and times. Never the request, never the answer.</summary>
    internal static object Describe(TextJob job, TextJobRegistry jobs, bool withClient = false) => new
    {
        id = job.Id,
        client = withClient ? job.ClientId : null,
        kind = job.Kind,
        model = job.Model,
        status = job.Status,
        stage = job.Stage,
        detail = job.Detail,
        createdAt = job.CreatedAt,
        startedAt = job.StartedAt,
        updatedAt = job.UpdatedAt,
        completedAt = job.CompletedAt,
        node = job.NodeId is null ? null : new { id = job.NodeId, name = job.NodeName, reportedAt = job.NodeReportedAt, pieces = job.NodePieces },
        servedBy = job.ServedBy,
        attempts = job.Attempts,
        pieces = job.Pieces,
        tokens = job.Tokens,
        tokensPerSecond = job.TokensPerSecond,
        group = jobs.GroupOf(job),
        error = job.ErrorMessage is null || job.Status == TextJobStatus.Succeeded
            ? null
            : new { status = job.ErrorStatus, code = job.ErrorCode, message = job.ErrorMessage, retryAfterSeconds = job.RetryAfterSeconds }
    };
}
