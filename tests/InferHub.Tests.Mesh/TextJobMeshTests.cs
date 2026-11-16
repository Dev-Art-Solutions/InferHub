using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using InferHub.Coordinator.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 150 across a real wire: a job submitted over HTTP, sent through the real <see cref="Dispatcher"/> to a real
/// <c>CoordinatorConnection</c>, which reports on it while its backend holds the answer. What only this shows: that
/// the node is asked to report (the <c>watch</c> flag survives SignalR), that its report finds the job, that a
/// cancel reaches the backend, and that a node leaving ends the job.
/// </summary>
public class TextJobMeshTests
{
    private const string Body = """{"kind":"chat","request":{"model":"llama3","messages":[{"role":"user","content":"hi"}]}}""";

    [Fact]
    public async Task AJobIsFollowedFromSubmitToAnswerAndTheNodeReportsOnIt()
    {
        await using var mesh = await StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(mesh.Url) };

        var submitted = await http.PostAsync("/api/jobs", Json(Body));
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal($"/api/jobs/{id}", submitted.Headers.Location!.OriginalString);

        // The backend holds the answer: the engine has produced nothing, and the node says so itself.
        await mesh.WaitForAsync(() => mesh.Backend.Running == 1);
        JsonElement status = default;
        await mesh.WaitForAsync(() =>
        {
            status = http.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}").GetAwaiter().GetResult();
            return status.GetProperty("stage").GetString() == "preparing";
        });

        Assert.Equal("running", status.GetProperty("status").GetString());
        Assert.Equal(ConcurrentJobsMeshTests.JobMesh.NodeId, status.GetProperty("node").GetProperty("id").GetString());
        Assert.True(status.GetProperty("node").TryGetProperty("reportedAt", out _));

        // Not finished: what there is so far, and a 202.
        var early = await http.GetAsync($"/api/jobs/{id}/result");
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);

        var listed = await http.GetFromJsonAsync<JsonElement>("/api/jobs");
        Assert.Equal(id, listed.GetProperty("jobs")[0].GetProperty("id").GetGuid());

        // The events route says each change and ends on the terminal one.
        var events = http.GetStringAsync($"/api/jobs/{id}/events");
        mesh.Backend.Release();
        var stream = await events.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("event: status", stream);
        Assert.Contains("succeeded", stream);

        var result = await http.GetAsync($"/api/jobs/{id}/result");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("node", result.Headers.GetValues(InferenceCore.ServedByHeader).Single());
        var answer = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", answer.GetProperty("message").GetProperty("content").GetString());
        Assert.True(answer.GetProperty("done").GetBoolean());

        // Deleting a finished job drops it now; after that it is the 404 of a job that never existed.
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/jobs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/jobs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/jobs/{Guid.NewGuid()}")).StatusCode);

        // Assert the survival: the node is still registered after all of it.
        Assert.NotNull(mesh.Registry.FindConnectionIdByNodeId(ConcurrentJobsMeshTests.JobMesh.NodeId));
    }

    [Fact]
    public async Task ACancelledJobStopsTheBackendThatWasRunningIt()
    {
        await using var mesh = await StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(mesh.Url) };

        var submitted = await http.PostAsync("/api/jobs", Json(Body));
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await mesh.WaitForAsync(() => mesh.Backend.Running == 1);

        var cancelled = await http.DeleteAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        await mesh.WaitForAsync(() => mesh.Backend.Cancelled == 1);
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync($"/api/jobs/{id}/result")).StatusCode);
    }

    [Fact]
    public async Task ANodeThatLeavesEndsItsJobAsFailed()
    {
        await using var mesh = await StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(mesh.Url) };

        var submitted = await http.PostAsync("/api/jobs", Json(Body));
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await mesh.WaitForAsync(() => mesh.Backend.Running == 1);

        await mesh.StopNodeAsync();

        JsonElement status = default;
        await mesh.WaitForAsync(() =>
        {
            status = http.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}").GetAwaiter().GetResult();
            return status.GetProperty("status").GetString() is "failed" or "cancelled" or "succeeded";
        });

        // The node had sent nothing, so the hub's pre-stream failover looked for another node first and found none:
        // the job ends failed, and the result route answers its error.
        Assert.Equal("failed", status.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadGateway, (await http.GetAsync($"/api/jobs/{id}/result")).StatusCode);
    }

    [Fact]
    public async Task ABodyThatIsNotAJobIsRefusedAndTurnedOffIsA404()
    {
        await using var mesh = await StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(mesh.Url) };

        async Task<HttpStatusCode> Post(string body, string? header = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/jobs") { Content = Json(body) };

            if (header is not null)
            {
                request.Headers.Add(header, "docs");
            }

            return (await http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await Post("""{"model":"llama3"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Post("""{"kind":"embed","request":{"model":"llama3"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Post("""{"request":{"messages":[]}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(Body, InferenceEndpoints.RetrieveHeader));

        await using var off = await StartAsync(new JobsOptions { Enabled = false });
        using var offHttp = new HttpClient { BaseAddress = new Uri(off.Url) };
        Assert.Equal(HttpStatusCode.NotFound, (await offHttp.PostAsync("/api/jobs", Json(Body))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await offHttp.GetAsync("/api/jobs")).StatusCode);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static Task<ConcurrentJobsMeshTests.JobMesh> StartAsync(JobsOptions? options = null) =>
        ConcurrentJobsMeshTests.JobMesh.StartAsync(
            services: services =>
            {
                services.AddSingleton<AdmissionControl>();
                services.AddSingleton(sp => TestUsage.Meter(admission: sp.GetRequiredService<AdmissionControl>()));
                services.AddSingleton<IRequestQueue>(sp => TestUsage.Queue(sp.GetRequiredService<INodeRegistry>()));
                services.AddSingleton<IProviderDispatcher, NoProvider>();
                services.AddSingleton(Options.Create(options ?? new JobsOptions()));
                services.AddSingleton<TextJobRegistry>();
            },
            map: app => app.MapJobEndpoints());

    private sealed class NoProvider : IProviderDispatcher
    {
        public ProviderDecision Decide(string model, bool hasCapableNode, ProviderSteer steer) => ProviderDecision.No;

        public Task<ProviderResult> DispatchAsync(string kind, string rawJson, string model, bool stream, CancellationToken cancellationToken)
            => throw new InvalidOperationException("must not be called");
    }
}
