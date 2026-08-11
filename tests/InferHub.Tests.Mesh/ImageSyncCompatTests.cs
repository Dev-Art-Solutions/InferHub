using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// <c>/v1/images/generations</c> is unchanged for anyone who never reads phase 47's notes.
/// </summary>
/// <remarks>
/// The route became "submit a job and wait for it" internally, which is a refactor with no
/// observable difference — and this is the test that says so. It asserts the shape, the extras and
/// the statuses rather than a recorded byte string, because the two fields that legitimately move
/// per request (<c>created</c> and the base64 of an image with a fresh seed) would make a golden
/// file a test of the clock.
/// </remarks>
public class ImageSyncCompatTests
{
    [Fact]
    public async Task TheSynchronousResponseIsTheV314EnvelopeFieldForField()
    {
        await using var mesh = await ImageMesh.StartAsync();

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body(n: 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("node", response.Headers.GetValues("X-InferHub-Served-By").Single());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(
            new[] { "created", "data" },
            root.EnumerateObject().Select(property => property.Name).ToArray());

        Assert.True(root.GetProperty("created").GetInt64() > 0);

        var items = root.GetProperty("data").EnumerateArray().ToArray();

        Assert.Equal(2, items.Length);

        foreach (var item in items)
        {
            // `projection` joined this list in 3.17 and is the one deliberate addition since 3.14.
            // It is here rather than omitted for a flat recipe because an absent projection is
            // indistinguishable from a node too old to have one, and a client that has to tell
            // those apart has learnt nothing (phase-49 D4). Everything else is unchanged, and the
            // list is pinned exactly so the *next* addition is a decision rather than a drift.
            Assert.Equal(
                new[] { "b64_json", "size", "seed", "projection", "revised_prompt" },
                item.EnumerateObject().Select(property => property.Name).ToArray());

            Assert.Equal("flat", item.GetProperty("projection").GetString());

            var bytes = Convert.FromBase64String(item.GetProperty("b64_json").GetString()!);

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
            Assert.Equal((512, 512), ImageEndpointTests.PngHeader(bytes));
            Assert.Equal("512x512", item.GetProperty("size").GetString());
            Assert.True(item.GetProperty("seed").GetInt64() > 0);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("revised_prompt").ValueKind);
        }

        // Distinct seeds per image, exactly as in 3.14.
        Assert.Equal(2, items.Select(item => item.GetProperty("seed").GetInt64()).Distinct().Count());
    }

    [Fact]
    public async Task ASynchronousCallLeavesNothingBehindOnTheHub()
    {
        await using var mesh = await ImageMesh.StartAsync();

        await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());

        // Read-once applies to the synchronous route too, which is the point of routing it through
        // the same store: "the synchronous route quietly keeps a copy" is not a state this hub can
        // be in.
        Assert.Equal(0, mesh.Jobs.Store.RetainedBytes());
        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    [Fact]
    public async Task PastTheSyncBudgetTheAnswerIsA503ThatNamesTheAsyncRoute()
    {
        await using var mesh = await ImageMesh.StartAsync(
            configureImages: options => options.SyncMaxWaitSeconds = 1,
            workerArguments: ["--image-step-ms", "300"]);

        var response = await mesh.Client.PostAsJsonAsync("/v1/images/generations", ImageEndpointTests.Body());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Images:SyncMaxWaitSeconds", body);
        Assert.Contains("/api/images/jobs/", body);

        // The job is still running and the caller was handed the id that reaches it — nothing was
        // thrown away because an HTTP client got bored.
        Assert.Contains("/events", body);
    }
}
