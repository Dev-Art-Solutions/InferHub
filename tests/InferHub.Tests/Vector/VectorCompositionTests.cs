using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Coordinator.Vector.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests.Vector;

/// <summary>
/// DI-shape guard, no database. Builds the coordinator's vector composition under the postgres
/// provider (with a syntactically-valid but dead connection string) and asserts the mesh
/// services are absent — this is the test that catches a future refactor quietly re-coupling
/// <c>ReplicationCoordinator</c> / <c>HealingService</c> to <see cref="IVectorStore"/>.
/// Services are resolved directly, so the bootstrapper never runs.
/// </summary>
public class VectorCompositionTests
{
    private static IServiceCollection BuildCollection(string provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VectorStore:Enabled"] = "true",
                ["VectorStore:Provider"] = provider,
                ["VectorStore:DataDirectory"] = Path.Combine(Path.GetTempPath(), "inferhub-comp-" + Guid.NewGuid().ToString("N")),
                ["VectorStore:Postgres:ConnectionString"] = "Host=localhost;Port=5432;Database=inferhub;Username=x;Password=y",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // RetrievalPipeline's external collaborators — stubbed so we test the vector graph in isolation.
        // In the real app IRouter/IDispatcher are registered globally before the vector store; the
        // LLM reranker resolves them.
        services.AddSingleton<IEmbeddingDispatcher, StubEmbeddingDispatcher>();
        services.AddSingleton<IRouter, StubRouter>();
        services.AddSingleton<IDispatcher, StubDispatcher>();
        services.AddSingleton<Metrics>();
        services.AddInferHubVectorStore(configuration);
        return services;
    }

    private static ServiceProvider BuildProvider(string provider) => BuildCollection(provider).BuildServiceProvider();

    [Fact]
    public void PostgresProviderRegistersPostgresStoreAndNoMeshServices()
    {
        using var sp = BuildProvider("postgres");

        Assert.IsType<PostgresVectorStore>(sp.GetRequiredService<IVectorStore>());
        Assert.IsType<NullVectorQueryRouter>(sp.GetRequiredService<IVectorQueryRouter>());
        Assert.NotNull(sp.GetRequiredService<RetrievalPipeline>());
        Assert.NotNull(sp.GetRequiredService<ReplicaRegistry>()); // stays registered, kept empty

        // The mesh services must not be registered under postgres.
        Assert.Null(sp.GetService<LocalVectorStore>());
        Assert.Null(sp.GetService<ReplicationCoordinator>());
        Assert.Null(sp.GetService<HealingService>());

        // ...and none of them sneaks in as a hosted service either — except the bootstrapper.
        var hosted = sp.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, h => h is PostgresBootstrapper);
        Assert.DoesNotContain(hosted, h => h is ReplicationCoordinator or HealingService);
    }

    [Fact]
    public void LocalProviderRegistersLocalStoreAndMeshServices()
    {
        // Inspect descriptors rather than resolving — the local mesh services pull in
        // INodeRegistry / IHubContext, which are out of scope for a vector DI-shape test.
        var services = BuildCollection("local");

        Assert.Contains(services, d => d.ServiceType == typeof(LocalVectorStore));
        Assert.Contains(services, d => d.ServiceType == typeof(IVectorQueryRouter) && d.ImplementationType == typeof(VectorQueryRouter));
        Assert.Contains(services, d => d.ServiceType == typeof(ReplicationCoordinator));
        Assert.Contains(services, d => d.ServiceType == typeof(HealingService));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(PostgresVectorStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IVectorQueryRouter) && d.ImplementationType == typeof(NullVectorQueryRouter));
    }

    private sealed class StubEmbeddingDispatcher : IEmbeddingDispatcher
    {
        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult("{}");

        public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
            => Task.FromResult(Array.Empty<float>());
    }

    private sealed class StubRouter : IRouter
    {
        public RoutableNode? Route(string model, string? conversationKey = null, string? excludeConnectionId = null) => null;
    }

    private sealed class StubDispatcher : IDispatcher
    {
        public Task<InferHub.Shared.Contracts.InferenceResult> DispatchAsync(RoutableNode node, InferHub.Shared.Contracts.InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public Task<System.Threading.Channels.ChannelReader<InferHub.Shared.Contracts.InferenceChunk>> DispatchStreamAsync(RoutableNode node, InferHub.Shared.Contracts.InferenceJob job, CancellationToken cancellationToken)
            => throw new NotImplementedException();
        public bool Complete(InferHub.Shared.Contracts.InferenceResult result) => true;
        public bool WriteChunk(InferHub.Shared.Contracts.InferenceChunk chunk) => true;
        public void FailForConnection(string connectionId, Exception? exception) { }
    }
}
