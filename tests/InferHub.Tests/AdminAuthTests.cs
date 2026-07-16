using System.Net;
using InferHub.Coordinator.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class AdminAuthTests
{
    [Fact]
    public async Task AdminMiddlewareSkipsNonAdminPaths()
    {
        var middleware = NewMiddleware(out var nextCalled);
        var context = NewContext("/api/status", remoteIp: IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminMiddlewareRejectsMissingToken()
    {
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["secret"]);
        var context = NewContext("/api/admin/nodes", remoteIp: IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminMiddlewareRejectsInvalidToken()
    {
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["secret"]);
        var context = NewContext(
            "/api/admin/nodes",
            remoteIp: IPAddress.Parse("8.8.8.8"),
            authorization: "Bearer wrong");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminMiddlewareAcceptsValidToken()
    {
        var middleware = NewMiddleware(out var nextCalled, adminKeys: ["secret"]);
        var context = NewContext(
            "/api/admin/nodes",
            remoteIp: IPAddress.Parse("8.8.8.8"),
            authorization: "Bearer secret");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminMiddlewareDoesNotAcceptInferenceKey()
    {
        // Inference keys live in ApiKeys and must not unlock admin routes.
        var middleware = NewMiddleware(
            out var nextCalled,
            adminKeys: ["admin-secret"]);
        var context = NewContext(
            "/api/admin/nodes",
            remoteIp: IPAddress.Parse("8.8.8.8"),
            authorization: "Bearer inference-key");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminMiddlewareExemptsLoopbackWhenRequireAuthForLoopbackIsFalse()
    {
        var middleware = NewMiddleware(
            out var nextCalled,
            adminKeys: ["secret"],
            requireAuthForLoopback: false);
        var context = NewContext("/api/admin/nodes", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AdminMiddlewareEnforcesLoopbackWhenRequireAuthForLoopbackIsTrue()
    {
        var middleware = NewMiddleware(
            out var nextCalled,
            adminKeys: ["secret"],
            requireAuthForLoopback: true);
        var context = NewContext("/api/admin/nodes", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task BearerMiddlewareSkipsAdminPaths()
    {
        // The admin middleware is responsible for /api/admin auth; the bearer middleware
        // must hand those requests through untouched even when no inference keys are set.
        var middleware = NewBearerMiddleware(out var nextCalled, apiKeys: []);
        var context = NewContext("/api/admin/nodes", remoteIp: IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static AdminApiKeyMiddleware NewMiddleware(
        out Func<bool> nextCalled,
        IReadOnlyList<string>? adminKeys = null,
        bool requireAuthForLoopback = false)
    {
        var called = false;
        nextCalled = () => called;

        RequestDelegate next = ctx =>
        {
            called = true;
            return Task.CompletedTask;
        };

        var options = NewOptions(new ApiKeyOptions
        {
            AdminApiKeys = (adminKeys ?? Array.Empty<string>()).ToList(),
            RequireAuthForLoopback = requireAuthForLoopback
        });

        return new AdminApiKeyMiddleware(next, options, NullLogger<AdminApiKeyMiddleware>.Instance);
    }

    private static BearerApiKeyMiddleware NewBearerMiddleware(
        out Func<bool> nextCalled,
        IReadOnlyList<string>? apiKeys = null,
        bool requireAuthForLoopback = false)
    {
        var called = false;
        nextCalled = () => called;

        RequestDelegate next = ctx =>
        {
            called = true;
            return Task.CompletedTask;
        };

        var options = NewOptions(new ApiKeyOptions
        {
            ApiKeys = (apiKeys ?? Array.Empty<string>()).ToList(),
            RequireAuthForLoopback = requireAuthForLoopback
        });

        return new BearerApiKeyMiddleware(next, options, new ClientRegistry(options), NullLogger<BearerApiKeyMiddleware>.Instance);
    }

    private static HttpContext NewContext(string path, IPAddress remoteIp, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = remoteIp;

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static IOptionsMonitor<ApiKeyOptions> NewOptions(ApiKeyOptions options)
    {
        return new StaticOptionsMonitor(options);
    }

    private sealed class StaticOptionsMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
