using System.Net;
using InferHub.Coordinator.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// D2 regression cover. The bearer guard used to early-exit on anything that wasn't
/// <c>/api</c>; mapping <c>/v1</c> without widening it would have shipped an
/// unauthenticated inference API. If any of these go red, that is what happened.
/// </summary>
public class OpenAiAuthTests
{
    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    [InlineData("/v1/embeddings")]
    [InlineData("/v1/models")]
    public async Task OpenAiRoutesRejectMissingToken(string path)
    {
        var middleware = NewBearerMiddleware(out var nextCalled, apiKeys: ["secret"]);
        var context = NewContext(path, remoteIp: IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
    }

    [Fact]
    public async Task OpenAiRoutesRejectInvalidToken()
    {
        var middleware = NewBearerMiddleware(out var nextCalled, apiKeys: ["secret"]);
        var context = NewContext(
            "/v1/chat/completions",
            remoteIp: IPAddress.Parse("8.8.8.8"),
            authorization: "Bearer wrong");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task OpenAiRoutesAcceptValidClientKey()
    {
        var middleware = NewBearerMiddleware(out var nextCalled, apiKeys: ["secret"]);
        var context = NewContext(
            "/v1/chat/completions",
            remoteIp: IPAddress.Parse("8.8.8.8"),
            authorization: "Bearer secret");

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task OpenAiRoutesDoNotAcceptAdminKey()
    {
        // The three token scopes stay separate: an admin key is not an inference key.
        var middleware = NewBearerMiddleware(
            out var nextCalled,
            apiKeys: ["client-key"],
            adminKeys: ["admin-key"]);
        var context = NewContext(
            "/v1/chat/completions",
            remoteIp: IPAddress.Parse("8.8.8.8"),
            authorization: "Bearer admin-key");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task OpenAiRoutesRequireKeyOnLoopbackWhenRequireAuthForLoopbackIsTrue()
    {
        var middleware = NewBearerMiddleware(
            out var nextCalled,
            apiKeys: ["secret"],
            requireAuthForLoopback: true);
        var context = NewContext("/v1/chat/completions", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task OpenAiRoutesExemptLoopbackWhenRequireAuthForLoopbackIsFalse()
    {
        // Same exemption the Ollama surface has had since phase 1 — bare-metal curl works.
        var middleware = NewBearerMiddleware(
            out var nextCalled,
            apiKeys: ["secret"],
            requireAuthForLoopback: false);
        var context = NewContext("/v1/chat/completions", remoteIp: IPAddress.Loopback);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthStaysOpen()
    {
        var middleware = NewBearerMiddleware(out var nextCalled, apiKeys: ["secret"]);
        var context = NewContext("/health", remoteIp: IPAddress.Parse("8.8.8.8"));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task UnauthorizedOpenAiResponseUsesTheOpenAiErrorEnvelope()
    {
        // An OpenAI SDK reads error.error.message; the Ollama envelope would surface to the
        // caller as an unhelpful "unknown error".
        var middleware = NewBearerMiddleware(out _, apiKeys: ["secret"]);
        var context = NewContext("/v1/chat/completions", remoteIp: IPAddress.Parse("8.8.8.8"));
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        var json = System.Text.Encoding.UTF8.GetString(body.ToArray());
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("missing bearer token", error.GetProperty("message").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("invalid_api_key", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task OllamaSurfaceKeepsItsOwnErrorEnvelope()
    {
        var middleware = NewBearerMiddleware(out _, apiKeys: ["secret"]);
        var context = NewContext("/api/chat", remoteIp: IPAddress.Parse("8.8.8.8"));
        var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        var json = System.Text.Encoding.UTF8.GetString(body.ToArray());
        using var document = System.Text.Json.JsonDocument.Parse(json);

        Assert.Equal("missing bearer token", document.RootElement.GetProperty("error").GetString());
    }

    private static BearerApiKeyMiddleware NewBearerMiddleware(
        out Func<bool> nextCalled,
        IReadOnlyList<string>? apiKeys = null,
        IReadOnlyList<string>? adminKeys = null,
        bool requireAuthForLoopback = false)
    {
        var called = false;
        nextCalled = () => called;

        RequestDelegate next = _ =>
        {
            called = true;
            return Task.CompletedTask;
        };

        var options = new StaticOptionsMonitor(new ApiKeyOptions
        {
            ApiKeys = (apiKeys ?? Array.Empty<string>()).ToList(),
            AdminApiKeys = (adminKeys ?? Array.Empty<string>()).ToList(),
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

    private sealed class StaticOptionsMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
