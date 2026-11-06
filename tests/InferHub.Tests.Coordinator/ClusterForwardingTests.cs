using System.Collections.Concurrent;
using System.Net;
using System.Text;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Images;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 132 D3–D4, across real sockets: a member passes on what its own nodes cannot serve, streams the answer back as it
/// comes, never passes a request on twice, serves it itself when the other member does not answer, and finds a job id on
/// whichever member holds it.
/// </summary>
public sealed class ClusterForwardingTests : IAsyncLifetime
{
    private static readonly Guid JobOnPeerB = Guid.Parse("0b6a9a9e-0000-4000-8000-000000000132");

    private readonly ConcurrentQueue<Seen> seenByB = new();
    private readonly TaskCompletionSource secondChunk = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WebApplication peerA = null!;
    private WebApplication peerB = null!;
    private WebApplication member = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        // hub-a holds no job and answers 404 to every job id; it is listed first, so a job lookup must go past it.
        peerA = await StartAsync(_ => { }, app => app.Run(context =>
        {
            context.Response.StatusCode = context.Request.Path.StartsWithSegments("/api/images/jobs") ? 404 : 500;
            return Task.CompletedTask;
        }));

        peerB = await StartAsync(_ => { }, app => app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            seenByB.Enqueue(new Seen(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers.Authorization.ToString(),
                context.Request.Headers[ClusterForwardingMiddleware.ForwardedByHeader].ToString(),
                await reader.ReadToEndAsync()));

            if (context.Request.Path.StartsWithSegments("/api/images/jobs"))
            {
                context.Response.StatusCode = context.Request.Path.Value!.Contains(JobOnPeerB.ToString()) ? 200 : 404;
                await context.Response.WriteAsync("job on hub-b");
                return;
            }

            context.Response.Headers["X-Peer"] = "hub-b";
            context.Response.Headers[ClusterRoleMiddleware.RoleHeader] = "active";
            context.Response.ContentType = "application/x-ndjson";
            await context.Response.WriteAsync("first\n");
            await context.Response.Body.FlushAsync();
            await secondChunk.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await context.Response.WriteAsync("second\n");
        }));

        member = await StartAsync(
            builder =>
            {
                var membership = new ClusterMembership("hub-m", activeActive: true);
                membership.MarkActive(0, DateTimeOffset.UtcNow);
                var registry = new NodeRegistry();
                var now = DateTimeOffset.UtcNow;
                registry.Upsert("c1", new NodeRegistration("n1", "node-1", "http://localhost:11434/", "3.92.0"), now);
                registry.ReportModels("c1", new NodeModels("n1", [new ModelInfo("local-model", "d", 1)], now), now);
                var peers = new ClusterPeers();
                peers.Replace(
                [
                    new ClusterMemberRow("hub-a", Address(peerA), TimeSpan.Zero, "[]"),
                    new ClusterMemberRow("hub-b", Address(peerB), TimeSpan.Zero,
                        ClusterPeers.Describe([new PeerModel(new ModelInfo("peer-model", "p", 2), null), new PeerModel(new ModelInfo("local-model", "x", 3), null)])),
                    new ClusterMemberRow("hub-dead", "http://127.0.0.1:1/", TimeSpan.Zero,
                        ClusterPeers.Describe([new PeerModel(new ModelInfo("dead-model", "q", 4), null)]))
                ], "hub-m", TimeSpan.FromSeconds(15));

                builder.Services.AddSingleton<IClusterMembership>(membership);
                builder.Services.AddSingleton(peers);
                builder.Services.AddSingleton<INodeRegistry>(registry);
                builder.Services.AddSingleton(Jobs(registry));
                builder.Services.AddHttpClient(ClusterForwardingMiddleware.ClientName, http => http.Timeout = Timeout.InfiniteTimeSpan);
            },
            app =>
            {
                app.UseMiddleware<ClusterForwardingMiddleware>();
                app.Run(async context =>
                {
                    using var reader = new StreamReader(context.Request.Body);
                    context.Response.Headers["X-Served-Here"] = "hub-m";
                    await context.Response.WriteAsync("here:" + await reader.ReadToEndAsync());
                });
            });

        client = new HttpClient { BaseAddress = new Uri(Address(member)) };
    }

    public async Task DisposeAsync()
    {
        secondChunk.TrySetResult();
        client.Dispose();

        foreach (var app in new[] { member, peerB, peerA })
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AModelOnlyAnotherMembersNodesHoldIsServedThereAndStreamedBackAsItComes()
    {
        using var request = Chat("peer-model", "/v1/chat/completions?x=1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "client-key");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var body = new StreamReader(await response.Content.ReadAsStreamAsync());

        // The first chunk arrives while hub-b is still holding the second back: nothing on the way buffers the answer.
        Assert.Equal("first", await body.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        secondChunk.TrySetResult();
        Assert.Equal("second", await body.ReadLineAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hub-b", response.Headers.GetValues(ClusterForwardingMiddleware.ForwardedToHeader).Single());
        Assert.Equal("hub-b", response.Headers.GetValues("X-Peer").Single());
        Assert.False(response.Headers.Contains(ClusterRoleMiddleware.RoleHeader), "the other member's role is not this one's");

        var seen = Assert.Single(seenByB);
        Assert.Equal("/v1/chat/completions?x=1", seen.PathAndQuery);
        Assert.Equal("Bearer client-key", seen.Authorization);
        Assert.Equal("hub-m", seen.ForwardedBy);
        Assert.Contains("\"peer-model\"", seen.Body);
    }

    [Theory]
    [InlineData("local-model", null)]
    [InlineData("nobody-has-it", null)]
    [InlineData("peer-model", "hub-x")]
    public async Task ThisMembersOwnNodesAnUnknownModelAndAPassedOnRequestAreServedHere(string model, string? forwardedBy)
    {
        using var request = Chat(model);

        if (forwardedBy is not null)
        {
            request.Headers.Add(ClusterForwardingMiddleware.ForwardedByHeader, forwardedBy);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal("hub-m", response.Headers.GetValues("X-Served-Here").Single());
        Assert.Contains($"\"{model}\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(seenByB);
    }

    [Fact]
    public async Task AMemberThatDoesNotAnswerLeavesTheRequestToThisOneWithItsBodyIntact()
    {
        using var response = await client.SendAsync(Chat("dead-model"));

        Assert.Equal("hub-m", response.Headers.GetValues("X-Served-Here").Single());
        Assert.Equal("here:{\"model\":\"dead-model\",\"stream\":true}", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, member.Services.GetRequiredService<ClusterPeers>().Forwarded().Single(entry => entry.Key == ("hub-dead", "unreachable")).Value);
    }

    [Fact]
    public async Task AJobIdThisMemberNeverIssuedIsFoundOnTheMemberThatHoldsIt()
    {
        using var held = await client.GetAsync($"/api/images/jobs/{JobOnPeerB}");
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        Assert.Equal("job on hub-b", await held.Content.ReadAsStringAsync());
        Assert.Equal("hub-b", held.Headers.GetValues(ClusterForwardingMiddleware.ForwardedToHeader).Single());

        using var nowhere = await client.GetAsync($"/api/images/jobs/{Guid.NewGuid()}");
        Assert.Equal("hub-m", nowhere.Headers.GetValues("X-Served-Here").Single());

        var counted = member.Services.GetRequiredService<ClusterPeers>().Forwarded().ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal(2, counted[("hub-a", "missing")]);
        Assert.Equal(1, counted[("hub-b", "missing")]);
        Assert.Equal(1, counted[("hub-b", "served")]);
    }

    private static HttpRequestMessage Chat(string model, string path = "/api/chat")
        => new(HttpMethod.Post, path) { Content = new StringContent($"{{\"model\":\"{model}\",\"stream\":true}}", Encoding.UTF8, "application/json") };

    private static ImageJobRegistry Jobs(NodeRegistry registry)
    {
        var routerOptions = Options.Create(new RouterOptions());
        return new ImageJobRegistry(
            Options.Create(new ImageEdgeOptions()),
            new Router(registry, new ConversationAffinity(routerOptions), new ThroughputTracker(), routerOptions),
            registry,
            null!,
            TestUsage.Meter(),
            new Metrics(),
            new NoImageJobArchive(),
            NullLogger<ImageJobRegistry>.Instance);
    }

    private static async Task<WebApplication> StartAsync(Action<WebApplicationBuilder> services, Action<WebApplication> pipeline)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        services(builder);
        var app = builder.Build();
        pipeline(app);
        await app.StartAsync();
        return app;
    }

    private static string Address(WebApplication app)
        => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";

    private sealed record Seen(string Method, string PathAndQuery, string Authorization, string ForwardedBy, string Body);
}
