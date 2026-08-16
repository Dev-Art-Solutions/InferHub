using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;

namespace InferHub.Tests;

/// <summary>
/// Phase 57 end to end: a real coordinator, a real node, a real SignalR connection and a real child
/// process, driven through OpenAI's Videos API.
/// </summary>
/// <remarks>
/// <para>
/// It is in <c>heavy-mesh</c> beside <c>ImageJobTests</c> because it shares their resource: one
/// worker pool, one queue, and assertions about queue position that a parallel megabyte-pushing
/// suite genuinely invalidates (phase 53's lesson, unchanged).
/// </para>
/// <para>
/// <b>What this cannot prove is that a video is a video.</b> The worker writes a real ISO-BMFF
/// container with no decodable samples in it, and every claim here is about the surface — the object,
/// the statuses, the progress, the cancel, the read-once, the metering, the wire. Whether Wan2.1
/// produces something worth watching is the published-image check's question and phase 60's.
/// </para>
/// </remarks>
[Collection("heavy-mesh")]
public class VideoJobTests
{
    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Polls the video object until it leaves <c>queued</c>/<c>in_progress</c>.</summary>
    private static async Task<JsonElement> SettleAsync(HttpClient client, string id, int attempts = 300)
    {
        for (var i = 0; i < attempts; i++)
        {
            var video = await ReadAsync(await client.GetAsync($"/v1/videos/{id}"));

            if (video.GetProperty("status").GetString() is not ("queued" or "in_progress"))
            {
                return video;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"video {id} never settled");
    }

    [Fact]
    public async Task AVideoIsSubmittedPolledFetchedOnceAndThenGone()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true);

        var created = await ReadAsync(await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = "a paper boat on a puddle, slow dolly in",
            seconds = 5,
            size = ImageFixture.VideoSize
        })));

        var id = created.GetProperty("id").GetString()!;

        Assert.StartsWith("video_", id);
        Assert.Equal("video", created.GetProperty("object").GetString());
        Assert.Equal(ImageFixture.VideoModel, created.GetProperty("model").GetString());
        Assert.Contains(created.GetProperty("status").GetString(), new[] { "queued", "in_progress" });

        var settled = await SettleAsync(mesh.Client, id);

        Assert.Equal("completed", settled.GetProperty("status").GetString());
        Assert.Equal(100, settled.GetProperty("progress").GetInt32());
        Assert.Equal(ImageFixture.VideoSize, settled.GetProperty("size").GetString());

        // 81 frames at 16 fps. The request said `seconds: 5` and the object reports what was
        // produced — the label named an offer and the measurement is the truth about the bytes.
        Assert.Equal(5.06, settled.GetProperty("seconds").GetDouble(), 2);
        Assert.True(settled.TryGetProperty("expires_at", out _));

        var content = await mesh.Client.GetAsync($"/v1/videos/{id}/content");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("video/mp4", content.Content.Headers.ContentType?.MediaType);

        var bytes = await content.Content.ReadAsByteArrayAsync();

        // A real container, checked by its own boxes rather than by the media type the response
        // claimed: `ftyp` at offset 4 is what makes this an mp4 and not a renamed PNG.
        Assert.True(bytes.Length > 64);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));

        // Read once. The second fetch is a 410 that says WHICH way the bytes went, not a 404 that
        // reads like a bug (47 D6).
        var again = await mesh.Client.GetAsync($"/v1/videos/{id}/content");

        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);

        var error = (await ReadAsync(again)).GetProperty("error");

        Assert.Equal("video_expired", error.GetProperty("code").GetString());
        Assert.Contains("delivered", error.GetProperty("message").GetString());

        // The job itself is still `completed` — the render happened, and calling it `failed` because
        // the bytes were collected would say it did not.
        var after = await ReadAsync(await mesh.Client.GetAsync($"/v1/videos/{id}"));

        Assert.Equal("completed", after.GetProperty("status").GetString());
        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    [Fact]
    public async Task ProgressClimbsWithTheWorkersStepsAndStopsShortOfOneHundred()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true, workerArguments: ["--video-step-ms", "40"]);

        var created = await ReadAsync(await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = "a kite over a field"
        })));

        var id = created.GetProperty("id").GetString()!;
        var seen = new List<int>();

        for (var i = 0; i < 200; i++)
        {
            var video = await ReadAsync(await mesh.Client.GetAsync($"/v1/videos/{id}"));
            var progress = video.GetProperty("progress").GetInt32();

            seen.Add(progress);

            if (video.GetProperty("status").GetString() == "completed")
            {
                break;
            }

            // The cap is the point: a client that sees 100 and stops polling has stopped one round
            // trip before the bytes exist.
            Assert.InRange(progress, 0, 99);
            await Task.Delay(25);
        }

        Assert.Contains(seen, value => value is > 0 and < 100);
        Assert.Equal(100, seen[^1]);
    }

    [Fact]
    public async Task ACancelledVideoStopsAndTheWorkerIsStillThereAfterwards()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true, workerArguments: ["--video-step-ms", "60"]);

        var created = await ReadAsync(await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = "a long slow pan"
        })));

        var id = created.GetProperty("id").GetString()!;

        for (var i = 0; i < 100; i++)
        {
            if ((await ReadAsync(await mesh.Client.GetAsync($"/v1/videos/{id}")))
                .GetProperty("status").GetString() == "in_progress")
            {
                break;
            }

            await Task.Delay(25);
        }

        var deleted = await mesh.Client.DeleteAsync($"/v1/videos/{id}");

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await ReadAsync(deleted)).GetProperty("deleted").GetBoolean());

        // DELETE means gone in this dialect, so the follow-up is a 404 rather than a 410 about a
        // retention window that had nothing to do with what happened.
        Assert.Equal(HttpStatusCode.NotFound, (await mesh.Client.GetAsync($"/v1/videos/{id}")).StatusCode);

        // The assertion the whole cancel design exists for: cancel is a FRAME, not a kill, so the
        // worker is still alive and the node is still registered — and a second job proves it by
        // running (47 D3).
        Assert.True(mesh.NodeIsRegistered());

        var second = await ReadAsync(await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = "the next caller's request"
        })));

        Assert.Equal(
            "completed",
            (await SettleAsync(mesh.Client, second.GetProperty("id").GetString()!)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnUnofferedDurationAndAnOffGridSizeAreBothRefusedWithTheListNamed()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true);

        // The duration is the WORKER's refusal, arriving as `invalid_request` and rendered as a 400
        // (46 D6: a recipe is a file on the node and the hub has no catalogue). It names the list,
        // because "no" with no alternative sends somebody to the docs.
        var duration = await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = "six seconds please",
            seconds = 6
        }));

        var settled = await SettleAsync(mesh.Client, (await ReadAsync(duration)).GetProperty("id").GetString()!);

        Assert.Equal("failed", settled.GetProperty("status").GetString());
        Assert.Contains("It offers: 2, 3, 4, 5", settled.GetProperty("error").GetProperty("message").GetString());

        // The size is the EDGE's refusal, because it is arithmetic no catalogue is needed for — and
        // 840x480 is a size the images API would have accepted.
        var size = await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = "an odd size",
            size = "840x480"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, size.StatusCode);
        Assert.Contains("multiple of 16", (await ReadAsync(size)).GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task AModelNoNodeServesUnderVideoIsRefusedBeforeAnythingIsQueued()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true);

        // `sd-test` exists on this fleet — under `image`. Asking for it as a video is a fleet-state
        // answer (phase-40 D4), not a "no such model": the 503 carries a Retry-After because a node
        // that serves it may connect at any time.
        var response = await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.Model,
            prompt = "a still that thinks it is a film"
        }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);

        // And nothing was queued — a job nothing on the fleet can run would be a queue position that
        // means nothing.
        Assert.Empty(mesh.Jobs.Store.ForClient(mesh.ClientId));
    }

    [Fact]
    public async Task AVideoIsMeteredInBothUnitsAndTheImagesListingDoesNotShowIt()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true);

        var created = await ReadAsync(await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = ImageFixture.KnownPrompt,
            seconds = 2
        })));

        var id = created.GetProperty("id").GetString()!;

        Assert.Equal("completed", (await SettleAsync(mesh.Client, id)).GetProperty("status").GetString());

        // Read back through the AGGREGATE rather than the raw rows, because the aggregate is where a
        // new unit is easiest to lose: it has a column per kind on purpose (42), so a `video_seconds`
        // row summed into `megapixel_steps` would be invisible in every other assertion.
        var aggregate = Assert.Single(
            await mesh.Ledger.QueryAsync(new InferHub.Coordinator.Services.UsageQuery()));

        // Two rows on one job, phase 42's audio shape: the card's cost and the clip's length. Adding
        // them together would be a number wrong in a way no reader could detect.
        Assert.Equal(2, aggregate.Requests);
        Assert.True(aggregate.MegapixelSteps > 0);
        Assert.Equal(2.0625, aggregate.VideoSeconds, 3);
        Assert.Equal(0, aggregate.TotalTokens);

        // Phase 51's images listing is scoped to the image kinds (57 D10). A video row in the Images
        // panel would render as a picture that will not load.
        var listing = await ReadAsync(await mesh.Client.GetAsync("/api/images/jobs"));

        Assert.Empty(listing.GetProperty("jobs").EnumerateArray());

        // And the id does not work on the other surface either, in either direction.
        var guid = id["video_".Length..];

        Assert.Equal(HttpStatusCode.NotFound, (await mesh.Client.GetAsync($"/api/images/jobs/{guid}")).StatusCode);
    }

    [Fact]
    public async Task ListingAndRemixAreRefusedWithASentenceRatherThanA404()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true);

        var listing = await mesh.Client.GetAsync("/v1/videos");
        var remix = await mesh.Client.PostAsJsonAsync("/v1/videos/video_abc/remix", new { prompt = "again" });

        // A 404 would read as "this hub is too old"; a 501 that names the reason is what 46 D5 does
        // about response_format=url.
        Assert.Equal(HttpStatusCode.NotImplemented, listing.StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, remix.StatusCode);
        Assert.Contains("enumerate", (await ReadAsync(listing)).GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("rule 7", (await ReadAsync(remix)).GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task NothingAnywhereInTheVideoPathEverWritesThePrompt()
    {
        await using var mesh = await ImageMesh.StartAsync(video: true);

        var created = await ReadAsync(await mesh.Client.PostAsync("/v1/videos", Body(new
        {
            model = ImageFixture.VideoModel,
            prompt = ImageFixture.KnownPrompt,
            negative_prompt = "blurry, watermark"
        })));

        var id = created.GetProperty("id").GetString()!;

        Assert.Equal("completed", (await SettleAsync(mesh.Client, id)).GetProperty("status").GetString());
        await mesh.Client.GetAsync($"/v1/videos/{id}/content");

        // Rule 7's fourth kind of content, asserted the way phases 42 and 46 assert theirs: a real
        // request through a real mesh with a capturing logger at Trace.
        var log = string.Join(Environment.NewLine, mesh.Logs.Lines);

        Assert.DoesNotContain(ImageFixture.KnownPrompt, log);
        Assert.DoesNotContain("blurry, watermark", log);
        Assert.DoesNotContain("lighthouse", log);

        // And not in the ledger either: a UsageRecord is a client, a model, a kind and two numbers,
        // and there is deliberately no field a prompt could hide in (25 D3).
        Assert.DoesNotContain(
            await mesh.Ledger.QueryAsync(new InferHub.Coordinator.Services.UsageQuery()),
            row => row.Model.Contains("lighthouse", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(0, mesh.ScratchEntryCount());
    }
}
