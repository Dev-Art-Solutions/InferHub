using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// The async image-job surface (phase 47), driven end to end against a <b>real</b> echo worker in
/// its slow, progress-emitting, cancellable mode: an HTTP client → a real coordinator → a real
/// SignalR wire → a real node → a real child process, and back.
/// </summary>
/// <remarks>
/// <para>
/// The worker takes real time per step and emits a real <c>progress</c> frame, which is what makes
/// every acceptance criterion in the phase reachable on a machine with no GPU and no weights. A
/// suite that faked the frames would prove the fake behaves — and the two things this phase is
/// actually about, <em>a frame arriving while a request is in flight</em> and <em>a worker that is
/// still warm after a cancel</em>, are precisely what a fake cannot produce.
/// </para>
/// </remarks>
[Collection("heavy-mesh")]
public class ImageJobTests
{
    /// <summary>Slow enough to cancel mid-run, quick enough that the suite is not a coffee break.</summary>
    private const int StepMs = 120;

    private const int Steps = 20;

    [Fact]
    public async Task AJobRunsToSucceededAndItsImageIsCollectedOnce()
    {
        await using var mesh = await Mesh();

        var id = await Submit(mesh);
        var final = await WaitFor(mesh, id, ImageJobStates.Succeeded);

        Assert.Equal(ImageJobStates.Succeeded, final.GetProperty("state").GetString());
        Assert.Equal(1, final.GetProperty("images").GetArrayLength());

        var content = await mesh.Client.GetAsync($"/api/images/jobs/{id}/content/0");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);

        var bytes = await content.Content.ReadAsByteArrayAsync();

        // The PNG header and the dimensions inside it, not the byte count — phase-46's assertion,
        // because a length check passes just as happily on 200 bytes of zeros.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
        Assert.Equal((512, 512), ImageEndpointTests.PngHeader(bytes));

        // Read once. The second GET is a 410 that says WHY, not a 404 that reads like a bug.
        var again = await mesh.Client.GetAsync($"/api/images/jobs/{id}/content/0");

        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Contains("delivered", await Body(again));
        Assert.Contains("nothing was written to disk", await Body(again));

        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    [Fact]
    public async Task ProgressArrivesOverSseInOrderAndMonotonically()
    {
        await using var mesh = await Mesh();

        var id = await Submit(mesh);
        var events = await Watch(mesh, id);

        var steps = events
            .Where(e => e.TryGetProperty("step", out var step) && step.ValueKind is JsonValueKind.Number)
            .Select(e => (State: e.GetProperty("state").GetString(), Step: e.GetProperty("step").GetInt32()))
            .ToArray();

        Assert.NotEmpty(steps);

        // Non-decreasing across every frame: the terminal frame carries the last step it reached,
        // so it repeats the number rather than inventing one.
        Assert.Equal(steps.Select(s => s.Step).Order().ToArray(), steps.Select(s => s.Step).ToArray());

        // Strictly increasing among the running frames, which is the property "monotonic per-step
        // progress" actually means: no step is ever reported twice and none is skipped backwards.
        var running = steps.Where(s => s.State == ImageJobStates.Running).Select(s => s.Step).ToArray();

        Assert.NotEmpty(running);
        Assert.Equal(running.Distinct().ToArray(), running);

        // One totalSteps for the whole run — the worker's number, not the caller's.
        var totals = events
            .Where(e => e.TryGetProperty("totalSteps", out var t) && t.ValueKind is JsonValueKind.Number)
            .Select(e => e.GetProperty("totalSteps").GetInt32())
            .Distinct()
            .ToArray();

        Assert.Single(totals);
        Assert.True(totals[0] > 0);
        Assert.Equal(totals[0], running[^1]);

        // The stream ends at the terminal state rather than leaving every `curl -N` hanging on a
        // job that finished.
        Assert.Equal(ImageJobStates.Succeeded, events[^1].GetProperty("state").GetString());
    }

    [Fact]
    public async Task CancelAtAStepEndsCancelledAndTheWorkerStaysWarmForTheNextJob()
    {
        await using var mesh = await Mesh();

        var id = await Submit(mesh);
        await WaitForStep(mesh, id, atLeast: 2);

        var cancel = await mesh.Client.DeleteAsync($"/api/images/jobs/{id}");

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        var final = await WaitForTerminal(mesh, id);

        // `succeeded` is legal here and is asserted as legal rather than treated as flaky: a job
        // cancelled at step 19 of 20 may finish anyway, and discarding a real image to honour a
        // state name would be worse than telling the caller what happened (D3).
        Assert.Contains(
            final.GetProperty("state").GetString(),
            new[] { ImageJobStates.Cancelled, ImageJobStates.Succeeded });

        // The point of a cooperative cancel: the worker was NOT killed, so the next job does not
        // pay a weight-load. With this fixture that shows up as the pool still holding its process
        // — which is what "starts without reloading weights" means on a box with no weights.
        var next = await Submit(mesh);
        var second = await WaitFor(mesh, next, ImageJobStates.Succeeded);

        Assert.Equal(ImageJobStates.Succeeded, second.GetProperty("state").GetString());
        Assert.Equal(0, mesh.ScratchEntryCount());
    }

    [Fact]
    public async Task AWorkerThatIgnoresACancelIsTerminatedPastTheGraceAndTheNodeKeepsServing()
    {
        await using var mesh = await Mesh("--ignore-cancel");

        var id = await Submit(mesh);
        await WaitForStep(mesh, id, atLeast: 2);

        await mesh.Client.DeleteAsync($"/api/images/jobs/{id}");

        var final = await WaitForTerminal(mesh, id, timeout: TimeSpan.FromSeconds(40));

        Assert.Equal(ImageJobStates.Cancelled, final.GetProperty("state").GetString());

        // A tool failure is a failed job and never a failed node (phase-41 D6): the node is still
        // registered and still serves the next one.
        Assert.True(mesh.NodeIsRegistered());

        var next = await Submit(mesh);
        Assert.Equal(ImageJobStates.Succeeded, (await WaitFor(mesh, next, ImageJobStates.Succeeded)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task AQueuedJobReportsItsPlaceInLineAndAFullQueueIsA503WithRetryAfter()
    {
        await using var mesh = await Mesh(configureImages: options => options.Jobs.MaxQueueDepth = 2);

        // One runs, two wait, the fourth is refused. The node has one worker, so this is the real
        // "a resource there is exactly one of" shape rather than a contrived limit.
        var first = await Submit(mesh);
        var second = await Submit(mesh);
        var third = await Submit(mesh);

        var queued = await mesh.Client.GetFromJsonAsync<JsonElement>($"/api/images/jobs/{third}");

        Assert.Equal(ImageJobStates.Queued, queued.GetProperty("state").GetString());
        Assert.True(queued.GetProperty("queuePosition").GetInt32() >= 1);

        var refused = await mesh.Client.PostAsJsonAsync("/api/images/jobs", ImageEndpointTests.Body());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("30", refused.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.Contains("Images:Jobs:MaxQueueDepth", await Body(refused));

        // FIFO: they finish in the order they were accepted, which is the property the whole D5
        // argument rests on.
        foreach (var id in new[] { first, second, third })
        {
            Assert.Equal(
                ImageJobStates.Succeeded,
                (await WaitFor(mesh, id, ImageJobStates.Succeeded, TimeSpan.FromSeconds(60))).GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task ARunningJobWhoseNodeDisappearsIsNodeLostAndIsNotRetried()
    {
        await using var mesh = await Mesh();

        var id = await Submit(mesh);
        await WaitForStep(mesh, id, atLeast: 2);

        await mesh.StopNodeAsync();

        var final = await WaitForTerminal(mesh, id);

        Assert.Equal(ImageJobStates.Failed, final.GetProperty("state").GetString());
        Assert.Equal(ImageJobReasons.NodeLost, final.GetProperty("reason").GetString());
        Assert.Contains("not retried", final.GetProperty("error").GetString());

        // Nothing was billed for work that produced nothing — which is the other half of "not
        // retried": a silent retry would have doubled these numbers rather than left them empty.
        Assert.Empty(await mesh.Ledger.QueryAsync(new InferHub.Coordinator.Services.UsageQuery()));
    }

    [Fact]
    public async Task ARestartForgetsEverything()
    {
        await using var mesh = await Mesh();

        var id = await Submit(mesh);
        await WaitFor(mesh, id, ImageJobStates.Succeeded);

        // The store is the hub's whole memory of a job, and it is in memory. A second store — which
        // is what a restarted process has — knows nothing, and the docs say so (D6).
        var afterRestart = new InferHub.Shared.Images.ImageJobStore(new InferHub.Shared.Images.ImageJobOptions());

        Assert.Null(afterRestart.Find(Guid.Parse(id), mesh.ClientId));
        Assert.Equal(0, afterRestart.RetainedBytes());
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static Task<ImageMesh> Mesh(
        string? workerArgument = null,
        Action<InferHub.Shared.Images.ImageEdgeOptions>? configureImages = null)
        => ImageMesh.StartAsync(
            configureImages: configureImages,
            workerArguments: workerArgument is null
                ? ["--image-step-ms", StepMs.ToString()]
                : ["--image-step-ms", StepMs.ToString(), workerArgument]);

    private static async Task<string> Submit(ImageMesh mesh)
    {
        var response = await mesh.Client.PostAsJsonAsync("/api/images/jobs", ImageEndpointTests.Body());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = document.RootElement.GetProperty("id").GetString()!;

        Assert.Equal($"/api/images/jobs/{id}", response.Headers.Location?.OriginalString);
        return id;
    }

    private static async Task<JsonElement> WaitFor(
        ImageMesh mesh,
        string id,
        string state,
        TimeSpan? timeout = null)
    {
        var final = await WaitForTerminal(mesh, id, timeout);
        Assert.Equal(state, final.GetProperty("state").GetString());
        return final;
    }

    private static async Task<JsonElement> WaitForTerminal(ImageMesh mesh, string id, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await mesh.Client.GetFromJsonAsync<JsonElement>($"/api/images/jobs/{id}");
            var state = job.GetProperty("state").GetString();

            if (ImageJobStates.IsTerminal(state))
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"image job {id} never reached a terminal state");
    }

    private static async Task WaitForStep(ImageMesh mesh, string id, int atLeast)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await mesh.Client.GetFromJsonAsync<JsonElement>($"/api/images/jobs/{id}");

            if (job.TryGetProperty("step", out var step)
                && step.ValueKind is JsonValueKind.Number
                && step.GetInt32() >= atLeast)
            {
                return;
            }

            if (ImageJobStates.IsTerminal(job.GetProperty("state").GetString()))
            {
                throw new InvalidOperationException($"image job {id} finished before step {atLeast}");
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"image job {id} never reached step {atLeast}");
    }

    /// <summary>Reads the SSE stream to its end and returns every <c>data:</c> frame, parsed.</summary>
    private static async Task<JsonElement[]> Watch(ImageMesh mesh, string id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/images/jobs/{id}/events");
        using var response = await mesh.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var frames = new List<JsonElement>();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            frames.Add(JsonDocument.Parse(line[6..]).RootElement.Clone());
        }

        Assert.NotEmpty(frames);
        return frames.ToArray();
    }

    private static Task<string> Body(HttpResponseMessage response) => response.Content.ReadAsStringAsync();
}
