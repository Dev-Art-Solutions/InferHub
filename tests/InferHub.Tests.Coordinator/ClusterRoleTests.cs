using System.Text.Json;
using InferHub.Coordinator.Cluster;
using InferHub.Coordinator.Endpoints;
using Microsoft.AspNetCore.Http;

namespace InferHub.Tests;

/// <summary>
/// What a client and a load balancer see (phase 32): the role header, the standby refusal, and
/// the promise that <c>Cluster:Enabled=false</c> is byte-identical to v2.13.
/// </summary>
public class ClusterRoleTests
{
    [Theory]
    [InlineData("/api/chat")]
    [InlineData("/api/generate")]
    [InlineData("/api/embed")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/embeddings")]
    [InlineData("/api/collections/docs/search")]
    [InlineData("/api/vector/docs")]
    // The node hub too: this is where node failover is enforced, because an exception from
    // NodeHub.OnConnectedAsync does not fail the client's StartAsync.
    [InlineData("/hubs/node/negotiate")]
    public async Task AStandbyRefusesWork(string path)
    {
        var response = await InvokeAsync(Standby(), path);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter);
        Assert.Equal(ClusterRoleMiddleware.StandbyRole, response.Headers[ClusterRoleMiddleware.RoleHeader]);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/status")]
    [InlineData("/api/nodes")]
    [InlineData("/api/tags")]
    [InlineData("/metrics")]
    [InlineData("/api/admin/nodes")]
    [InlineData("/status.html")]
    public async Task AStandbyStillServesOperationalRoutes(string path)
    {
        var response = await InvokeAsync(Standby(), path);

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(ClusterRoleMiddleware.StandbyRole, response.Headers[ClusterRoleMiddleware.RoleHeader]);
    }

    [Fact]
    public async Task AnActiveCoordinatorServesEverythingAndSaysSo()
    {
        var response = await InvokeAsync(Active(), "/v1/chat/completions");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(ClusterRoleMiddleware.ActiveRole, response.Headers[ClusterRoleMiddleware.RoleHeader]);
    }

    [Fact]
    public async Task ASingleCoordinatorDeploymentHasNoRoleHeaderAtAll()
    {
        // Cluster:Enabled=false must be indistinguishable from v2.13 on the wire — a new header on
        // every response is a wire change for deployments that never asked for HA.
        var response = await InvokeAsync(new SingleCoordinatorMembership(), "/api/chat");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.False(response.Headers.ContainsKey(ClusterRoleMiddleware.RoleHeader));
    }

    [Fact]
    public async Task TheStandbyRefusalUsesTheOpenAiEnvelopeOnV1()
    {
        // An SDK reads error.message; an Ollama-shaped body surfaces there as "unknown error",
        // which is the failure the OpenAI envelope exists to prevent (phase 21/29).
        var body = await BodyOfAsync(Standby(), "/v1/chat/completions");

        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("api_error", error.GetProperty("type").GetString());
        Assert.Equal("standby_coordinator", error.GetProperty("code").GetString());
        Assert.Contains("standby", error.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task TheStandbyRefusalUsesTheOllamaEnvelopeOnApi()
    {
        var body = await BodyOfAsync(Standby(), "/api/chat");

        using var document = JsonDocument.Parse(body);
        Assert.Contains("standby", document.RootElement.GetProperty("error").GetString()!);
    }

    [Fact]
    public void TheStatusBlockIsAbsentWithoutCluster()
    {
        Assert.Null(StatusEndpoint.BuildClusterBlock(new SingleCoordinatorMembership()));

        var block = StatusEndpoint.BuildClusterBlock(Active());
        Assert.NotNull(block);
        Assert.Equal(ClusterRoleMiddleware.ActiveRole, block!.Role);
        Assert.Equal("hub-a", block.Instance);
    }

    private static ClusterMembership Standby()
    {
        var membership = new ClusterMembership("hub-a");
        membership.MarkStandby("the lease is held by 'hub-b'");
        return membership;
    }

    private static ClusterMembership Active()
    {
        var membership = new ClusterMembership("hub-a");
        membership.MarkActive(3, DateTimeOffset.UtcNow);
        return membership;
    }

    /// <summary>Runs the middleware over a bare context whose terminal delegate always 200s.</summary>
    internal static async Task<HttpResponse> InvokeAsync(IClusterMembership membership, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        var middleware = new ClusterRoleMiddleware(
            next => { next.Response.StatusCode = StatusCodes.Status200OK; return Task.CompletedTask; },
            membership);

        await middleware.InvokeAsync(context);
        return context.Response;
    }

    private static async Task<string> BodyOfAsync(IClusterMembership membership, string path)
    {
        var response = await InvokeAsync(membership, path);
        response.Body.Position = 0;
        return await new StreamReader(response.Body).ReadToEndAsync();
    }
}
