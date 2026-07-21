using System.Net;
using System.Net.Http.Json;
using System.Text;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 31. Two tenants, one mesh, one vector store — and no way for either to observe the
/// other's corpus on any path that names a collection. The denials are asserted as <c>404</c>
/// with the same sentence a missing collection produces: a <c>403</c> would answer the question
/// "does tenant B have a collection called this?", which is the question tenancy exists to refuse.
/// </summary>
public class CollectionAccessTests
{
    private static readonly ResolvedClient TenantA = new("tenant-a", null, ["tenant-a-docs", "shared-*"]);
    private static readonly ResolvedClient TenantB = new("tenant-b", null, ["tenant-b-docs"]);

    // ---- the policy itself ----------------------------------------------------------------

    [Fact]
    public void AClientWithNoScopeSeesEveryCollection()
    {
        // The pre-v2.13 contract, and the default. A config that never heard of scoping is
        // unchanged — this is the assertion that keeps that promise honest.
        Assert.True(CollectionAccessPolicy.CanAccess(ResolvedClient.Anonymous, "anything"));
        Assert.True(CollectionAccessPolicy.CanAccess(new ResolvedClient("named", null), "anything"));
        Assert.True(CollectionAccessPolicy.CanAccess(new ResolvedClient("named", null, []), "anything"));
    }

    [Fact]
    public void AnExactScopeMatchesOnlyThatCollection()
    {
        Assert.True(CollectionAccessPolicy.CanAccess(TenantB, "tenant-b-docs"));
        Assert.False(CollectionAccessPolicy.CanAccess(TenantB, "tenant-a-docs"));
        Assert.False(CollectionAccessPolicy.CanAccess(TenantB, "tenant-b-docs-2"));
    }

    [Fact]
    public void OnlyATrailingStarIsAWildcard()
    {
        Assert.True(CollectionAccessPolicy.CanAccess(TenantA, "shared-glossary"));
        Assert.True(CollectionAccessPolicy.CanAccess(TenantA, "shared-"));
        Assert.False(CollectionAccessPolicy.CanAccess(TenantA, "not-shared-glossary"));

        // A star anywhere else is a literal, not a glob — a config that expected regex-ish
        // matching should fail closed rather than open a namespace nobody meant to grant.
        var odd = new ResolvedClient("odd", null, ["a*b"]);
        Assert.True(CollectionAccessPolicy.CanAccess(odd, "a*b"));
        Assert.False(CollectionAccessPolicy.CanAccess(odd, "azzzb"));
    }

    [Fact]
    public void ScopeMatchingIsCaseInsensitiveLikeCollectionLookup()
    {
        Assert.True(CollectionAccessPolicy.CanAccess(TenantB, "TENANT-B-DOCS"));
        Assert.True(CollectionAccessPolicy.CanAccess(TenantA, "Shared-Glossary"));
    }

    [Fact]
    public void ScopeIsResolvedFromTheClientConfig()
    {
        var options = NewOptions(new ClientConfig
        {
            Id = "tenant-a",
            Key = "key-a",
            Collections = ["tenant-a-*"]
        });

        var resolved = new ClientRegistry(options).Resolve("key-a");

        Assert.NotNull(resolved);
        Assert.Equal("tenant-a", resolved!.Id);
        Assert.True(CollectionAccessPolicy.CanAccess(resolved, "tenant-a-docs"));
        Assert.False(CollectionAccessPolicy.CanAccess(resolved, "tenant-b-docs"));
    }

    [Fact]
    public void AFlatApiKeyStillResolvesToTheUnscopedAnonymousIdentity()
    {
        var options = NewOptions();
        options.CurrentValue.ApiKeys.Add("flat");

        var resolved = new ClientRegistry(options).Resolve("flat");

        Assert.Equal(ResolvedClient.Anonymous, resolved);
        Assert.True(CollectionAccessPolicy.CanAccess(resolved!, "tenant-a-docs"));
    }

    // ---- the enforcement, over a real route ------------------------------------------------

    [Theory]
    [InlineData("GET", "/api/collections/{c}/documents")]
    [InlineData("POST", "/api/collections/{c}/documents")]
    [InlineData("GET", "/api/collections/{c}/documents/doc-1")]
    [InlineData("GET", "/api/collections/{c}/documents/doc-1/chunks")]
    [InlineData("DELETE", "/api/collections/{c}/documents/doc-1")]
    [InlineData("POST", "/api/collections/{c}/search")]
    [InlineData("POST", "/api/vector/{c}/upsert")]
    [InlineData("POST", "/api/vector/{c}/query")]
    [InlineData("GET", "/api/vector/{c}/rec-1")]
    [InlineData("DELETE", "/api/vector/{c}/rec-1")]
    public async Task EveryCollectionPathIsClosedToTheOtherTenant(string method, string template)
    {
        await using var host = await ScopedHost.StartAsync();

        var denied = await host.SendAsync(method, template.Replace("{c}", "tenant-a-docs"), "key-b");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(
            "collection 'tenant-a-docs' does not exist",
            (await denied.Content.ReadFromJsonAsync<ErrorBody>())!.Error);

        var allowed = await host.SendAsync(method, template.Replace("{c}", "tenant-b-docs"), "key-b");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task ACollectionOutsideScopeReadsTheSameWhetherOrNotItExists()
    {
        // The check runs before the store is ever consulted, so "tenant A's collection" and
        // "no such collection anywhere" are the same 404 with the same sentence. That is the
        // property that makes the boundary hold: B learns only about its own scope.
        await using var host = await ScopedHost.StartAsync();

        var real = await host.SendAsync("GET", "/api/collections/tenant-a-docs/documents", "key-b");
        var imaginary = await host.SendAsync("GET", "/api/collections/no-such-thing/documents", "key-b");

        Assert.Equal(real.StatusCode, imaginary.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, real.StatusCode);
        Assert.Equal(
            "collection 'tenant-a-docs' does not exist",
            (await real.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
        Assert.Equal(
            "collection 'no-such-thing' does not exist",
            (await imaginary.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task AnUnscopedKeySeesEveryCollectionExactlyAsBefore()
    {
        await using var host = await ScopedHost.StartAsync();

        foreach (var collection in new[] { "tenant-a-docs", "tenant-b-docs", "anything-else" })
        {
            var response = await host.SendAsync("GET", $"/api/collections/{collection}/documents", "flat-key");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task APrefixScopeGrantsTheWholeNamespace()
    {
        await using var host = await ScopedHost.StartAsync();

        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync("GET", "/api/collections/shared-glossary/documents", "key-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await host.SendAsync("GET", "/api/collections/tenant-a-docs/documents", "key-a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.SendAsync("GET", "/api/collections/tenant-b-docs/documents", "key-a")).StatusCode);
    }

    // ---- inline retrieval (the /api and /v1 RAG header) --------------------------------------

    [Fact]
    public void InlineRetrievalRefusesACollectionOutsideScope()
    {
        var context = NewRetrievalContext(TenantB, "tenant-a-docs");

        var ex = Assert.Throws<CollectionNotVisibleException>(
            () => InferenceEndpoints.TryReadRetrievalHeader(context, out _));

        Assert.Equal("tenant-a-docs", ex.Collection);
        Assert.Equal("collection 'tenant-a-docs' does not exist", ex.Message);
    }

    [Fact]
    public void InlineRetrievalHonoursACollectionInsideScope()
    {
        var context = NewRetrievalContext(TenantB, "tenant-b-docs");

        Assert.True(InferenceEndpoints.TryReadRetrievalHeader(context, out var retrieval));
        Assert.Equal("tenant-b-docs", retrieval.Collection);
    }

    [Fact]
    public void InlineRetrievalIsUnchangedForAnUnscopedClient()
    {
        var context = NewRetrievalContext(ResolvedClient.Anonymous, "tenant-a-docs");

        Assert.True(InferenceEndpoints.TryReadRetrievalHeader(context, out var retrieval));
        Assert.Equal("tenant-a-docs", retrieval.Collection);
    }

    private static HttpContext NewRetrievalContext(ResolvedClient client, string collection)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/chat";
        context.Request.Headers[InferenceEndpoints.RetrieveHeader] = collection;
        context.Items[BearerApiKeyMiddleware.ClientItemKey] = client;
        return context;
    }

    private static StaticApiKeyOptions NewOptions(params ClientConfig[] clients)
        => new(new ApiKeyOptions { Clients = clients.ToList() });

    private sealed record ErrorBody(string Error);

    /// <summary>
    /// A real Kestrel host with the real bearer middleware and the real group filter, so the
    /// route values, the 404 body and the middleware ordering are all exercised rather than
    /// assumed. The handlers are stubs — what is under test is who is allowed to reach them.
    /// </summary>
    private sealed class ScopedHost : IAsyncDisposable
    {
        private WebApplication app = null!;
        private HttpClient client = null!;

        public static async Task<ScopedHost> StartAsync()
        {
            var host = new ScopedHost();

            var options = new StaticApiKeyOptions(new ApiKeyOptions
            {
                ApiKeys = ["flat-key"],
                RequireAuthForLoopback = true,
                Clients =
                [
                    new ClientConfig { Id = "tenant-a", Key = "key-a", Collections = ["tenant-a-docs", "shared-*"] },
                    new ClientConfig { Id = "tenant-b", Key = "key-b", Collections = ["tenant-b-docs"] }
                ]
            });

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IOptionsMonitor<ApiKeyOptions>>(options);
            builder.Services.AddSingleton<IClientRegistry>(new ClientRegistry(options));

            host.app = builder.Build();
            host.app.UseMiddleware<BearerApiKeyMiddleware>();

            var documents = host.app.MapGroup("/api/collections/{collection}/documents").RequireCollectionScope();
            documents.MapPost("/", Ok);
            documents.MapGet("/", Ok);
            documents.MapGet("/{id}", Ok);
            documents.MapGet("/{id}/chunks", Ok);
            documents.MapDelete("/{id}", Ok);

            host.app.MapPost("/api/collections/{collection}/search", Ok).RequireCollectionScope();

            var vector = host.app.MapGroup("/api/vector/{collection}").RequireCollectionScope();
            vector.MapPost("/upsert", Ok);
            vector.MapPost("/query", Ok);
            vector.MapGet("/{id}", Ok);
            vector.MapDelete("/{id}", Ok);

            await host.app.StartAsync();
            host.client = new HttpClient { BaseAddress = new Uri(host.app.Urls.First()) };

            return host;
        }

        public Task<HttpResponseMessage> SendAsync(string method, string path, string key)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            request.Headers.Add("Authorization", "Bearer " + key);

            if (method is "POST")
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            return client.SendAsync(request);
        }

        private static IResult Ok() => Results.Ok(new { ok = true });

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class StaticApiKeyOptions(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
