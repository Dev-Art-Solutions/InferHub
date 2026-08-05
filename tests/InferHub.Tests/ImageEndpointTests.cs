using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// <c>/v1/images/generations</c>, end to end: an HTTP client → a real coordinator → a real SignalR
/// wire → a real node → a real child process → and back (phase 46).
/// </summary>
public class ImageEndpointTests
{
    [Fact]
    public async Task AGenerationRoundTripsThroughTheMeshAndReturnsTheOpenAiEnvelope()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(document.RootElement.GetProperty("created").GetInt64() > 0);

        var item = Assert.Single(document.RootElement.GetProperty("data").EnumerateArray());
        var bytes = Convert.FromBase64String(item.GetProperty("b64_json").GetString()!);

        // The PNG header and the dimensions inside it, not the byte count. A length assertion passes
        // just as happily on 200 bytes of zeros — phase 42's "listen to it", one modality over, and
        // the closest an automated assertion can honestly get to "it is a picture".
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal((512, 512), PngHeader(bytes));

        Assert.Equal("512x512", item.GetProperty("size").GetString());
        Assert.True(item.GetProperty("seed").GetInt64() > 0);
    }

    [Fact]
    public async Task ABatchReturnsThatManyDistinctImagesFromOneNode()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body(n: 3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = document.RootElement.GetProperty("data").EnumerateArray().ToArray();

        Assert.Equal(3, items.Length);

        // Distinct seeds, reported per image. Without them a caller cannot reproduce the one of the
        // three they liked, which is the whole reason `seed` is on the response at all.
        Assert.Equal(3, items.Select(i => i.GetProperty("seed").GetInt64()).Distinct().Count());
    }

    /// <summary>
    /// D5, and it is rule 4 and rule 7 pointing at the same refusal: serving a URL means the hub
    /// keeps the bytes, and keeping the bytes means an image store with a retention question nobody
    /// agreed to answer.
    /// </summary>
    [Fact]
    public async Task ResponseFormatUrlIsRefusedAndSaysWhy()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a cat", size = ImageFixture.Size, response_format = "url" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var message = await Message(response);
        Assert.Contains("never stores a generated image", message);
        Assert.Contains("b64_json", message);
    }

    [Theory]
    [InlineData("1020x1020", "multiple of 8")]
    [InlineData("1024", "WIDTHxHEIGHT")]
    [InlineData("32x32", "outside 64-4096")]
    public async Task AMalformedSizeIsRefusedAtTheEdgeBeforeAnyGpuTime(string size, string expected)
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a cat", size });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await Message(response));
    }

    /// <summary>
    /// A <em>well-formed</em> size the recipe was not trained on is the worker's call, not the
    /// edge's: a recipe is a file on the node and the hub has no model catalogue until phase 48. The
    /// worker names the buckets and the edge renders the 400 without reading the message (phase-29
    /// D6).
    /// </summary>
    [Fact]
    public async Task ASizeTheRecipeDoesNotSupportIsA400NamingTheOnesItDoes()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model, prompt = "a cat", size = "1024x768" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("512x512, 768x768, 1024x1024", await Message(response));

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("invalid_request", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AnNOverTheBatchCapIsRefusedNamingTheCap()
    {
        await using var mesh = await ImageMesh.StartAsync(maxBatch: 2);

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body(n: 3));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most 2 (Images:MaxBatch)", await Message(response));
    }

    /// <summary>
    /// The budget is checked <b>before</b> a step runs and the refusal names the largest <c>n</c>
    /// that fits, so a caller is not left bisecting. 512×512×4 bytes is exactly 1 MiB as an upper
    /// bound, so a 2.5 MiB budget takes two and refuses more.
    /// </summary>
    [Fact]
    public async Task ABatchOverTheByteBudgetIsRefusedWithTheLargestNThatFits()
    {
        await using var mesh = await ImageMesh.StartAsync(maxResponseBytes: (long)(2.5 * 1024 * 1024));

        var refused = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body(n: 4));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var message = await Message(refused);
        Assert.Contains("Images:MaxResponseBytes", message);
        Assert.Contains("the largest n is 2", message);

        // …and the largest n it named actually works, which is the half of this that a wrong
        // arithmetic slip would leave untested.
        var accepted = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body(n: 2));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    /// <summary>
    /// The clamp in <c>ImageEdgeOptions.Resolve</c>: a response budget larger than the attachment
    /// cap cannot be expressed, because the attachment cap is what sizes the SignalR receive limit
    /// and exceeding <em>that</em> tears the connection down rather than failing the message.
    /// </summary>
    [Fact]
    public async Task TheResponseBudgetCannotExceedTheAttachmentCap()
    {
        await using var mesh = await ImageMesh.StartAsync(
            maxAttachmentBytes: 2 * 1024 * 1024,
            maxResponseBytes: 512L * 1024 * 1024);

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body(n: 4));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("2097152-byte response budget", await Message(response));
    }

    [Theory]
    [InlineData("X-InferHub-Image-Steps", "many")]
    [InlineData("X-InferHub-Image-Steps", "0")]
    [InlineData("X-InferHub-Image-Guidance", "7,5")]
    [InlineData("X-InferHub-Image-Seed", "-1")]
    public async Task AnUnknownExtensionHeaderValueIsA400AndNeverASilentFallback(string header, string value)
    {
        await using var mesh = await ImageMesh.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = JsonContent.Create(Body())
        };

        request.Headers.TryAddWithoutValidation(header, value);

        var response = await mesh.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(header, await Message(response));
    }

    [Fact]
    public async Task TheStepsHeaderReachesTheWorkerAndChangesWhatIsMetered()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations")
        {
            Content = JsonContent.Create(Body())
        };

        request.Headers.TryAddWithoutValidation("X-InferHub-Image-Steps", "10");

        Assert.Equal(HttpStatusCode.OK, (await mesh.Client.SendAsync(request)).StatusCode);

        // 512 × 512 × 10 / 1e6 = 2.62144
        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(2.62144, row.MegapixelSteps, 5);
    }

    [Fact]
    public async Task ACapabilityNobodyProvidesIsA503WithRetryAfterAndAModelNobodyHoldsIsA404()
    {
        await using var mesh = await ImageMesh.StartAsync();

        // The model exists on the fleet — for `speak`, not for `image`.
        var wrongCapability = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = "speak-test", prompt = "a cat", size = ImageFixture.Size });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, wrongCapability.StatusCode);
        Assert.Equal("30", wrongCapability.Headers.GetValues("Retry-After").Single());
        Assert.Equal("no node currently provides 'image' for model 'speak-test'", await Message(wrongCapability));

        var unknown = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = "no-such-model", prompt = "a cat", size = ImageFixture.Size });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("model 'no-such-model' not found", await Message(unknown));
    }

    [Fact]
    public async Task AMissingPromptIsA400ThatNamesTheField()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync(
            "/v1/images/generations",
            new { model = ImageFixture.Model });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("prompt is required", await Message(response));
    }

    [Fact]
    public async Task AWorkerFailureWithNoCodeIsStillA502()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: ["--image-fail", "boom"]);

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    /// <summary>
    /// Phase-21 D2, and it is the reason this is a test rather than a comment: a client-facing route
    /// added under a prefix the guard does not cover ships an unauthenticated inference API, and
    /// this one occupies a GPU for a minute per call.
    /// </summary>
    [Fact]
    public async Task TheImageRouteRejectsAMissingKey()
    {
        var called = false;
        var options = new StaticOptionsMonitor(new ApiKeyOptions { ApiKeys = ["secret"] });

        var middleware = new BearerApiKeyMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            options,
            new ClientRegistry(options),
            NullLogger<BearerApiKeyMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/images/generations";
        context.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // ---- metering -------------------------------------------------------------------------------

    [Fact]
    public async Task AGenerationIsMeteredInMegapixelStepsAndNotInTokens()
    {
        await using var mesh = await ImageMesh.StartAsync();

        await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body(n: 2));

        // The worker's own numbers: 512 × 512 at 30 steps, twice → 2 × 7.86432
        var row = Assert.Single(await mesh.Ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(15.72864, row.MegapixelSteps, 5);
        Assert.Equal(0, row.TotalTokens);
        Assert.Equal(0, row.AudioSeconds);
        Assert.Equal(0, row.Characters);
    }

    [Fact]
    public async Task AFailedJobIsNotBilled()
    {
        await using var mesh = await ImageMesh.StartAsync(workerArguments: ["--image-fail", "boom"]);

        await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body());

        Assert.Empty(await mesh.Ledger.QueryAsync(new UsageQuery()));
    }

    /// <summary>
    /// A client whose only budget is tokens could otherwise keep a card busy indefinitely: image
    /// generation consumes no tokens, no seconds and no characters.
    /// </summary>
    [Fact]
    public async Task AnImageBudgetIsSeparateFromEveryOtherBudgetAndRejectsWith402()
    {
        await using var mesh = await ImageMesh.StartAsync(
            limits: new ClientLimits { MegapixelStepsPerDay = 5 });

        // Admission runs before routing and is fed post-completion, so the first request through a
        // fresh budget always lands — phase-25 D4's stated overshoot-by-one, unchanged here.
        var first = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // 512 × 512 × 30 / 1e6 = 7.86 landed against a budget of 5, so the next one is refused.
        var second = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body());
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        Assert.Contains("daily megapixel-step budget of 5", await Message(second));
        Assert.True(second.Headers.Contains("Retry-After"));

        using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("insufficient_quota", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AModelOutsideTheClientsAllowlistIsThe404ThatLooksLikeAMissingModel()
    {
        await using var mesh = await ImageMesh.StartAsync(
            limits: new ClientLimits { AllowedModels = ["something-else"] });

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", Body());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal($"model '{ImageFixture.Model}' not found", await Message(response));
    }

    // ---- helpers --------------------------------------------------------------------------------

    internal static object Body(int? n = null, string? size = null, string? prompt = null) => new
    {
        model = ImageFixture.Model,
        prompt = prompt ?? "a lighthouse in a storm, oil painting",
        size = size ?? ImageFixture.Size,
        n = n ?? 1
    };

    /// <summary>Reads width and height out of a PNG's IHDR, big-endian.</summary>
    internal static (int Width, int Height) PngHeader(byte[] png) => (
        (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19],
        (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);

    private static async Task<string?> Message(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString();
    }

    private sealed class StaticOptionsMonitor(ApiKeyOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
