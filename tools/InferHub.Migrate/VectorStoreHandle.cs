using InferHub.Coordinator.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InferHub.Migrate;

/// <summary>
/// A live <see cref="IVectorStore"/> for one side of the migration, composed through the
/// coordinator's own <c>AddInferHubVectorStore</c> — the single composition root
/// (<c>Program.cs</c> and the DI-shape test go through the same call). Reimplementing three
/// connectors in a tool would mean two places that know how a provider is built, and the tool's copy
/// would be the one that silently rots.
/// <para>
/// The provider's <see cref="IVectorStoreBootstrapper"/> is started by hand: that is what creates the
/// schema on an empty Postgres, warms the Qdrant metadata cache, and — the part that matters most for
/// a tool — fails fast with the same actionable message the coordinator would give if the store is
/// unreachable. Deliberately <em>only</em> that seam and not every <c>IHostedService</c>: under
/// <c>local</c> the hosted-service list also holds replication and healing, which want a node registry
/// and a dispatcher. This composes a store, not a hub.
/// </para>
/// </summary>
internal sealed class VectorStoreHandle : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<IVectorStoreBootstrapper> _started = [];

    private VectorStoreHandle(ServiceProvider services, IVectorStore store, string provider)
    {
        _services = services;
        Store = store;
        Provider = provider;
    }

    public IVectorStore Store { get; }

    public string Provider { get; }

    public static async Task<VectorStoreHandle> OpenAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        // Warning and above: a migration's output is its own progress report, not the store's
        // startup chatter. A refusal still surfaces — bootstrappers throw rather than log.
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        services.AddInferHubVectorStore(configuration);

        var provider = services.BuildServiceProvider();
        var store = provider.GetService<IVectorStore>()
            ?? throw new InvalidOperationException(
                "no vector store was composed from this configuration — check VectorStore:Provider and VectorStore:Enabled.");

        var handle = new VectorStoreHandle(
            provider, store, configuration["VectorStore:Provider"] ?? VectorStoreProviderExtensions.Local);

        foreach (var bootstrapper in provider.GetServices<IVectorStoreBootstrapper>())
        {
            await bootstrapper.StartAsync(cancellationToken);
            handle._started.Add(bootstrapper);
        }

        return handle;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var bootstrapper in _started)
        {
            try { await bootstrapper.StopAsync(CancellationToken.None); } catch { /* shutting down anyway */ }
        }
        await _services.DisposeAsync();
    }
}
