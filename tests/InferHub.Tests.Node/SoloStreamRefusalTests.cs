using System.Text;
using System.Text.Json;
using InferHub.Node.LocalApi;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 93, found against a real colibri: a solo node's OpenAI stream answered a backend that
/// refused <em>before the first frame</em> with a 200 and an empty <c>finish_reason=stop</c>. The
/// truncation is right once a client holds a 200 and part of an answer; before that, it turns a
/// refusal into what reads as an empty answer.
/// </summary>
public class SoloStreamRefusalTests
{
    [Fact]
    public async Task ARefusalBeforeTheFirstFrameIsA502WithTheUpstreamsSentence()
    {
        var context = Context();

        await new LocalApiEndpoints.LocalSseResult(
                Failing(after: 0),
                new ChatStreamFormatter("id", 0, "olmoe", includeUsage: false),
                NullLogger.Instance)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);

        var error = JsonDocument.Parse(Body(context)).RootElement.GetProperty("error");
        Assert.Equal(OpenAiErrorTypes.ApiError, error.GetProperty("type").GetString());
        Assert.Contains("Request body must be", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AFailureAfterTheFirstFrameStillTruncatesCleanly()
    {
        var context = Context();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedAfterFirstWrite(context));

        await new LocalApiEndpoints.LocalSseResult(
                Failing(after: 1),
                new ChatStreamFormatter("id", 0, "olmoe", includeUsage: false),
                NullLogger.Instance)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", Body(context));
    }

    /// <summary>
    /// What the real run actually hit: the executor does not throw, it yields the Ollama-shaped
    /// failure chunk every node sends — and the formatter rendered that as an empty stop.
    /// </summary>
    [Fact]
    public async Task TheExecutorsFailureChunkIsA502BeforeAnyFrame()
    {
        var context = Context();

        await new LocalApiEndpoints.LocalSseResult(
                Chunks(Error("Token penalties are not supported yet.")),
                new ChatStreamFormatter("id", 0, "olmoe", includeUsage: false),
                NullLogger.Instance)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(
            "Token penalties are not supported yet.",
            JsonDocument.Parse(Body(context)).RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheExecutorsFailureChunkAfterAFrameIsAnErrorFrameNotAStop()
    {
        var context = Context();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedAfterFirstWrite(context));

        await new LocalApiEndpoints.LocalSseResult(
                Chunks(Partial, Error("engine exited")),
                new ChatStreamFormatter("id", 0, "olmoe", includeUsage: false),
                NullLogger.Instance)
            .ExecuteAsync(context);

        var body = Body(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("data: {\"error\":{\"message\":\"engine exited\"", body);
        Assert.DoesNotContain("\"finish_reason\":\"stop\"", body);
        Assert.EndsWith(OpenAiSse.DoneFrame, body);
    }

    /// <summary>
    /// Phase 100: a refusal that is the caller's — a picture for a Strata size without the image
    /// encoder — carries its 400 on the failure chunk, and the solo edge answers with it.
    /// </summary>
    [Fact]
    public async Task ARefusalChunkWithA4xxIsThatStatusNotA502()
    {
        var context = Context();

        await new LocalApiEndpoints.LocalSseResult(
                Chunks(JsonSerializer.Serialize(new { error = "cannot read pictures", status = 400, done = true })),
                new ChatStreamFormatter("id", 0, "strata-q2_0", includeUsage: false),
                NullLogger.Instance)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var error = JsonDocument.Parse(Body(context)).RootElement.GetProperty("error");
        Assert.Equal(OpenAiErrorTypes.InvalidRequest, error.GetProperty("type").GetString());
        Assert.Equal("cannot read pictures", error.GetProperty("message").GetString());
    }

    private const string Partial = """{"model":"olmoe","message":{"role":"assistant","content":"Sofia"},"done":false}""";

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message, done = true });

    private static async IAsyncEnumerable<InferenceChunk> Chunks(params string[] json)
    {
        foreach (var item in json)
        {
            await Task.Yield();
            yield return new InferenceChunk(Guid.Empty, item, Done: item.Contains("\"done\":true"));
        }
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static async IAsyncEnumerable<InferenceChunk> Failing(int after)
    {
        for (var i = 0; i < after; i++)
        {
            yield return new InferenceChunk(
                Guid.Empty,
                """{"model":"olmoe","message":{"role":"assistant","content":"Sofia"},"done":false}""",
                Done: false);
        }

        await Task.Yield();
        throw new OpenAiUpstreamException(400, "the OpenAI-compatible upstream returned 400 BadRequest: Request body must be between 1 and 4194304 bytes.");
    }

    /// <summary>DefaultHttpContext never reports HasStarted; this one does once a byte is written.</summary>
    private sealed class StartedAfterFirstWrite(HttpContext context) : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => context.Response.Body.Length > 0;
    }
}
