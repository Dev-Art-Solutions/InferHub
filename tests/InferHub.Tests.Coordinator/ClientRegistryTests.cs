using InferHub.Coordinator.Auth;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

public class ClientRegistryTests
{
    [Fact]
    public void ANamedClientResolvesToItsIdAndLimits()
    {
        var registry = Registry(options =>
        {
            options.Clients.Add(new ClientConfig
            {
                Id = "acme",
                Key = "key-acme",
                Limits = new ClientLimits { RequestsPerMinute = 10 }
            });
        });

        var resolved = registry.Resolve("key-acme");

        Assert.NotNull(resolved);
        Assert.Equal("acme", resolved!.Id);
        Assert.Equal(10, resolved.Limits!.RequestsPerMinute);
    }

    [Fact]
    public void ALegacyFlatKeyResolvesAnonymousAndUnlimited()
    {
        // D1: nobody's config breaks. A flat Auth:ApiKeys entry is a client with no name and
        // no limits — exactly what every key was before phase 25.
        var registry = Registry(options => options.ApiKeys.Add("old-key"));

        var resolved = registry.Resolve("old-key");

        Assert.NotNull(resolved);
        Assert.Equal(ResolvedClient.Anonymous.Id, resolved!.Id);
        Assert.Null(resolved.Limits);
    }

    [Fact]
    public void AnUnknownTokenResolvesToNull()
    {
        var registry = Registry(options =>
        {
            options.ApiKeys.Add("real");
            options.Clients.Add(new ClientConfig { Id = "acme", Key = "named" });
        });

        Assert.Null(registry.Resolve("wrong"));
    }

    [Fact]
    public void NoConfiguredKeysMeansNothingResolves()
    {
        Assert.Null(Registry(_ => { }).Resolve("anything"));
        Assert.False(Registry(_ => { }).HasAnyKeys);
    }

    [Fact]
    public void ADuplicateKeyAcrossTwoClientsFailsValidation()
    {
        var options = new ApiKeyOptions();
        options.Clients.Add(new ClientConfig { Id = "one", Key = "same" });
        options.Clients.Add(new ClientConfig { Id = "two", Key = "same" });

        var result = new ApiKeyOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ambiguous", result.FailureMessage);
    }

    [Fact]
    public void AFlatKeyDuplicatingANamedClientFailsValidation()
    {
        // It would resolve to the named client and silently stop being anonymous.
        var options = new ApiKeyOptions();
        options.Clients.Add(new ClientConfig { Id = "acme", Key = "shared" });
        options.ApiKeys.Add("shared");

        var result = new ApiKeyOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("acme", result.FailureMessage);
    }

    [Fact]
    public void AClientWithoutAnIdOrKeyFailsValidation()
    {
        var noId = new ApiKeyOptions();
        noId.Clients.Add(new ClientConfig { Id = " ", Key = "k" });
        Assert.True(new ApiKeyOptionsValidator().Validate(null, noId).Failed);

        var noKey = new ApiKeyOptions();
        noKey.Clients.Add(new ClientConfig { Id = "acme", Key = "" });
        Assert.True(new ApiKeyOptionsValidator().Validate(null, noKey).Failed);
    }

    [Fact]
    public void ALegacyOnlyConfigPassesValidationUntouched()
    {
        var options = new ApiKeyOptions();
        options.ApiKeys.Add("plain");

        Assert.True(new ApiKeyOptionsValidator().Validate(null, options).Succeeded);
    }

    private static ClientRegistry Registry(Action<ApiKeyOptions> configure)
    {
        var options = new ApiKeyOptions();
        configure(options);
        return new ClientRegistry(new StaticMonitor(options));
    }

    private sealed class StaticMonitor(ApiKeyOptions value) : IOptionsMonitor<ApiKeyOptions>
    {
        public ApiKeyOptions CurrentValue { get; } = value;

        public ApiKeyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyOptions, string?> listener) => null;
    }
}
