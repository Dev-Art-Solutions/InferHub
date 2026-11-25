using InferHub.Coordinator.Services;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Shards;
using InferHub.Node.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 149: found on the published 3.113.0 images — <c>docker stop</c> on a member ended in an unhandled
/// <c>ObjectDisposedException</c> from <see cref="ShardSeeder"/>. The node and the hub register each shard service twice, as
/// itself and forwarded as a hosted service, so the container disposes the one instance twice; the second dispose cancelled a
/// token source the first had already disposed. Each test runs the shipped wiring — start, stop, dispose the container — and
/// asks only that a stop is quiet.
/// </summary>
public sealed class ShardShutdownTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-shutdown-" + Guid.NewGuid().ToString("N"));

    public ShardShutdownTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>As <c>NodeHostBuilderExtensions</c> and the coordinator's <c>Program</c> do it: the container owns the instance.</summary>
    private static async Task StartStopAndDisposeAsync<T>(Func<T> create) where T : class, IHostedService
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => create());
        services.AddHostedService(provider => provider.GetRequiredService<T>());
        var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        foreach (var service in hosted)
        {
            await service.StopAsync(CancellationToken.None);
        }

        await provider.DisposeAsync();
    }

    [Fact]
    public Task AMemberThatFillsItsCacheStopsQuietly() =>
        StartStopAndDisposeAsync(() => new ShardSeeder(new ShardOptions { Enabled = true, RamGiB = 4 }, root, Path.Combine(root, "cache"),
            NullLogger.Instance, null));

    [Fact]
    public Task AHeadStopsQuietly() =>
        StartStopAndDisposeAsync(() => new ShardGroupHost(new ShardOptions { Enabled = true, RamGiB = 3, AllowHead = true }, root, () => "dotnet",
            () => [], (_, _) => throw new InvalidOperationException("no group is started here"), _ => Task.FromResult(true), NullLogger.Instance, null));

    [Fact]
    public Task ATunnelWaitingForItsCertificateStopsQuietly() =>
        StartStopAndDisposeAsync(() => new ShardTunnel(new ShardOptions { Enabled = true, RamGiB = 1 }, () => null, () => null,
            NullLogger.Instance, null, TimeSpan.FromMilliseconds(200)));

    [Fact]
    public Task AWorkerThatRanStopsQuietly() =>
        StartStopAndDisposeAsync(() => new ShardWorker(new ShardOptions { Enabled = true, RamGiB = 1, Executable = "dotnet" }, new BackendOptions(),
            NullLogger.Instance, null, _ => true, null));

    [Fact]
    public Task TheHubsRelayStopsQuietly() =>
        StartStopAndDisposeAsync(() => new ShardRelay(new ShardRelayOptions { Enabled = true, Port = 0 }, NullLogger.Instance));
}
