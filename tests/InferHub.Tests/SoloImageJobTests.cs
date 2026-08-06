using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// The same five job routes on a standalone node (phase 47; phase-41 D8), through the real
/// <c>NodeHostFactory</c>, the real composition root and a real echo child process.
/// </summary>
/// <remarks>
/// A developer who wrote against a hub and then ran one <c>docker run</c> on their own box should
/// not have to change a line — so this drives the same paths and asserts the same shapes as
/// <see cref="ImageJobTests"/>. The parts a caller can observe are the coordinator's own classes
/// rather than a second implementation, which is what makes that true by construction; this is the
/// test that says the routes are actually <em>reachable</em>, which no amount of shared code proves.
/// </remarks>
public class SoloImageJobTests
{
    private const int StepMs = 120;

    [Fact]
    public async Task ASoloJobRunsToSucceededAndItsImageIsCollectedOnce()
    {
        var (host, cleanup) = await Solo();

        try
        {
            var id = await Submit(host);
            var final = await WaitForTerminal(host, id);

            Assert.Equal(ImageJobStates.Succeeded, final.GetProperty("state").GetString());

            var content = await host.Client.GetAsync($"/api/images/jobs/{id}/content/0");

            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);

            var bytes = await content.Content.ReadAsByteArrayAsync();

            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
            Assert.Equal((512, 512), ImageEndpointTests.PngHeader(bytes));

            // Read-once holds identically on a box with no coordinator anywhere.
            var again = await host.Client.GetAsync($"/api/images/jobs/{id}/content/0");

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
    public async Task ProgressAndCancelBehaveTheSameWithNoCoordinatorAnywhere()
    {
        var (host, cleanup) = await Solo();

        try
        {
            var id = await Submit(host);
            await WaitForStep(host, id, atLeast: 2);

            var cancel = await host.Client.DeleteAsync($"/api/images/jobs/{id}");

            Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

            var final = await WaitForTerminal(host, id);

            // `succeeded` is legal here for the same reason it is on a hub (D3).
            Assert.Contains(
                final.GetProperty("state").GetString(),
                new[] { ImageJobStates.Cancelled, ImageJobStates.Succeeded });

            // The worker stayed warm, so the box goes straight on serving.
            var next = await Submit(host);

            Assert.Equal(
                ImageJobStates.Succeeded,
                (await WaitForTerminal(host, next)).GetProperty("state").GetString());
        }
        finally
        {
            await host.DisposeAsync();
            cleanup.Dispose();
        }
    }

    [Fact]
    public async Task AJobIdThatDoesNotExistIsA404AndAModelNothingServesIsA503()
    {
        var (host, cleanup) = await Solo();

        try
        {
            var missing = await host.Client.GetAsync($"/api/images/jobs/{Guid.NewGuid()}");

            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            // The node's own words — "this node", not "no node": on a standalone box those are the
            // same sentence and only one of them is true.
            var unserved = await host.Client.PostAsJsonAsync(
                "/api/images/jobs",
                new { model = "nothing-serves-this", prompt = "x", size = ImageFixture.Size });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, unserved.StatusCode);

            var body = await unserved.Content.ReadAsStringAsync();

            Assert.Contains("this node does not provide", body);
            Assert.Equal("30", unserved.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        }
        finally
        {
            await host.DisposeAsync();
            cleanup.Dispose();
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static Task<(SoloHost Host, IDisposable Cleanup)> Solo() =>
        ImageFixture.SoloAsync("--image-step-ms", StepMs.ToString());

    private static async Task<string> Submit(SoloHost host)
    {
        var response = await host.Client.PostAsJsonAsync("/api/images/jobs", ImageEndpointTests.Body());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;

        Assert.Equal($"/api/images/jobs/{id}", response.Headers.Location?.OriginalString);
        return id;
    }

    private static async Task<JsonElement> WaitForTerminal(SoloHost host, string id)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(45);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await host.Client.GetFromJsonAsync<JsonElement>($"/api/images/jobs/{id}");

            if (ImageJobStates.IsTerminal(job.GetProperty("state").GetString()))
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"solo image job {id} never reached a terminal state");
    }

    private static async Task WaitForStep(SoloHost host, string id, int atLeast)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await host.Client.GetFromJsonAsync<JsonElement>($"/api/images/jobs/{id}");

            if (job.TryGetProperty("step", out var step)
                && step.ValueKind is JsonValueKind.Number
                && step.GetInt32() >= atLeast)
            {
                return;
            }

            if (ImageJobStates.IsTerminal(job.GetProperty("state").GetString()))
            {
                throw new InvalidOperationException($"solo image job {id} finished before step {atLeast}");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"solo image job {id} never reached step {atLeast}");
    }
}
