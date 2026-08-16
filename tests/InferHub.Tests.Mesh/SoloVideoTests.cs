using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace InferHub.Tests;

/// <summary>
/// OpenAI's Videos API on a standalone node (phase 57; phase-41 D8), through the real
/// <c>NodeHostFactory</c>, the real composition root and a real echo child process.
/// </summary>
/// <remarks>
/// Solo gets the surface on the same day the hub does, and the parts a caller can observe are the
/// coordinator's own classes rather than a second implementation — which is what makes parity true
/// by construction. This is the test that says the routes are <em>reachable</em> here, which no
/// amount of shared code proves: phase 47's five job routes shipped mapped-and-unreachable in solo
/// once, and it was the pull-and-run that found it.
/// </remarks>
public class SoloVideoTests
{
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    [Fact]
    public async Task ASoloVideoRunsToCompletedAndItsBytesAreCollectedOnce()
    {
        var (host, cleanup) = await ImageFixture.SoloVideoAsync();

        try
        {
            var created = await ReadAsync(await host.Client.PostAsync("/v1/videos", Body(new
            {
                model = ImageFixture.VideoModel,
                prompt = "a paper boat on a puddle",
                seconds = 3
            })));

            var id = created.GetProperty("id").GetString()!;

            Assert.StartsWith("video_", id);
            Assert.Equal("video", created.GetProperty("object").GetString());

            JsonElement settled = default;

            for (var i = 0; i < 200; i++)
            {
                settled = await ReadAsync(await host.Client.GetAsync($"/v1/videos/{id}"));

                if (settled.GetProperty("status").GetString() is not ("queued" or "in_progress"))
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.Equal("completed", settled.GetProperty("status").GetString());

            // 49 frames at 16 fps: the request's `seconds: 3` named an offer, and 3.06 is what the
            // clip runs for. The same arithmetic the hub reports, from the same renderer.
            Assert.Equal(3.06, settled.GetProperty("seconds").GetDouble(), 2);

            var content = await host.Client.GetAsync($"/v1/videos/{id}/content");

            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal("video/mp4", content.Content.Headers.ContentType?.MediaType);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(await content.Content.ReadAsByteArrayAsync(), 4, 4));

            // Read-once holds identically on a box with no coordinator anywhere.
            var again = await host.Client.GetAsync($"/v1/videos/{id}/content");

            Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
            Assert.Contains("nothing was written to disk", await again.Content.ReadAsStringAsync());
        }
        finally
        {
            await host.DisposeAsync();
            cleanup.Dispose();
        }
    }

    [Fact]
    public async Task ANodeThatOffersNoVideoAnswersTheSameFiveHundredAndThreeItAnswersForImages()
    {
        // The plain image fixture — no `video` in its manifest. Phase-40 D4's split: a capability
        // nobody provides is fleet state and gets the saturation shape, never a 404 that would read
        // as "no such model".
        var (host, cleanup) = await ImageFixture.SoloAsync();

        try
        {
            var response = await host.Client.PostAsync("/v1/videos", Body(new
            {
                model = ImageFixture.VideoModel,
                prompt = "nothing here can film"
            }));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
            Assert.Equal(
                "capability_unavailable",
                (await ReadAsync(response)).GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            await host.DisposeAsync();
            cleanup.Dispose();
        }
    }

    [Fact]
    public async Task ListingAndRemixAreRefusedHereTooRatherThanBeingAbsent()
    {
        var (host, cleanup) = await ImageFixture.SoloVideoAsync();

        try
        {
            // A 404 on a solo node and a 501 on a hub would be exactly the parity difference phase 42
            // shipped and a suite found late.
            Assert.Equal(HttpStatusCode.NotImplemented, (await host.Client.GetAsync("/v1/videos")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotImplemented,
                (await host.Client.PostAsJsonAsync("/v1/videos/video_abc/remix", new { prompt = "again" })).StatusCode);
        }
        finally
        {
            await host.DisposeAsync();
            cleanup.Dispose();
        }
    }
}
