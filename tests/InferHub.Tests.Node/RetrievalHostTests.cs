using InferHub.Node.Configuration;
using InferHub.Node.Retrieval;
using InferHub.Shared.Vector;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 44, D3: the corpus starts and stops under a node that keeps serving, and a start that fails
/// leaves <b>no</b> corpus rather than half of one.
/// </summary>
/// <remarks>
/// The alternative this phase rejected — restarting the node to apply retrieval config — would have
/// made every one of these a process lifecycle test instead, which is the point: a hub instruction
/// must not be indistinguishable from a crash in an operator's logs.
/// </remarks>
public class RetrievalHostTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<RetrievalHost> hosts = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }

        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ANodeWithNoProfileHasNoCorpusAndItsRoutesRefuse()
    {
        var host = Host();

        // Not "an empty corpus": none. That is what the 501 on the retrieval routes reports, and it
        // is a different fact from a corpus with nothing in it.
        Assert.Null(host.Current);
        Assert.Null(host.TryLease());

        await host.StartAsync(CancellationToken.None);

        Assert.Null(host.Current);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task StartingAndStoppingAreLiveAndLeaveNoCorpusBehind()
    {
        var host = Host();

        var started = await host.StartCorpusAsync(Local(), CancellationToken.None);

        Assert.True(started.Started);
        Assert.Null(started.Error);
        Assert.NotNull(host.Current);

        using (var lease = host.TryLease())
        {
            Assert.NotNull(lease);
            await lease!.Corpus.Store.CreateCollectionAsync("docs", dimension: 2, distance: "cosine");
        }

        await host.StopCorpusAsync(CancellationToken.None);

        Assert.Null(host.Current);
        Assert.Null(host.TryLease());
    }

    [Fact]
    public async Task RestartingWithADifferentConfigurationSwapsTheCorpusRatherThanStackingOne()
    {
        var host = Host();

        await host.StartCorpusAsync(Local(), CancellationToken.None);
        var first = host.Current;

        await host.StartCorpusAsync(Local(embeddingModel: "another-embed"), CancellationToken.None);
        var second = host.Current;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // The old corpus is gone, not merely shadowed: two stores over one data directory is two
        // writers over one append log, and the second one wins in a way nobody can reconstruct.
        Assert.Null(first!.TryLease());
        Assert.NotNull(second!.TryLease());
    }

    [Fact]
    public async Task AFailedStartLeavesNoPartialCorpusAndSaysWhy()
    {
        var host = Host();

        // Phase-44 D2: postgres is refused by name, with the reason, rather than reported as an
        // unknown value. An operator who typed the name of a provider this product genuinely has is
        // owed the reason it is not available on a box.
        var refused = await host.StartCorpusAsync(
            new CorpusRequest("postgres", null, null, null, null, CorpusRequest.Profile),
            CancellationToken.None);

        Assert.False(refused.Started);
        Assert.Contains("Npgsql", refused.Error);
        Assert.Null(host.Current);
        Assert.Equal(refused.Error, host.LastError);
    }

    [Fact]
    public async Task AnUnresolvableCredentialIsARefusalNamingTheKeyRatherThanAnUnauthenticatedCorpus()
    {
        var host = Host();

        var refused = await host.StartCorpusAsync(
            new CorpusRequest("qdrant", "http://qdrant.example:6333", "sofia-qdrant", null, null, CorpusRequest.Profile),
            CancellationToken.None);

        // D4: the hub names a credential and the node resolves it. A name this box does not have
        // must never fall back to an unauthenticated connection to somebody's engine.
        Assert.False(refused.Started);
        Assert.Contains("sofia-qdrant", refused.Error);
        Assert.Contains("Credentials", refused.Error);
        Assert.Null(host.Current);
    }

    [Fact]
    public async Task AnUnreachableEngineFailsTheStartWithItsAddressRatherThanEveryQueryLater()
    {
        var host = Host(options =>
        {
            options.Credentials["edge-key"] = "s3cret";

            // A short timeout so the suite spends two seconds proving this rather than thirty.
            options.Qdrant.TimeoutSeconds = 2;
        });

        // Port 1 is reserved and nothing listens on it; the reachability probe is the same round
        // trip QdrantBootstrapper makes on the hub.
        var refused = await host.StartCorpusAsync(
            new CorpusRequest("qdrant", "http://127.0.0.1:1", "edge-key", null, null, CorpusRequest.Profile),
            CancellationToken.None);

        Assert.False(refused.Started);
        Assert.Contains("127.0.0.1:1", refused.Error);
        Assert.Null(host.Current);
    }

    [Fact]
    public async Task AnInFlightRetrievalDrainsRatherThanFaultingWhenTheCorpusStops()
    {
        var host = Host();
        await host.StartCorpusAsync(Local(), CancellationToken.None);

        var lease = host.TryLease();
        Assert.NotNull(lease);

        // The stop must not complete while a retrieval is still holding the store.
        var stopping = host.StopCorpusAsync(CancellationToken.None);
        var finishedEarly = await Task.WhenAny(stopping, Task.Delay(300)) == stopping;

        Assert.False(finishedEarly);

        // The request that was already running still has its store, and finishes against it.
        Assert.NotNull(await lease!.Corpus.Store.ListCollectionsAsync(CancellationToken.None));

        lease.Dispose();
        await stopping;

        Assert.Null(host.Current);
    }

    [Fact]
    public async Task TheCorpusStateIsWhatThisNodeKnowsAndNeverAQueryTheHubRan()
    {
        var host = Host();

        var off = await host.StateAsync("node-1", CancellationToken.None);
        Assert.False(off.Enabled);
        Assert.Equal(InferHub.Shared.Contracts.NodeCorpusState.Stopped, off.Status);
        Assert.Empty(off.Collections);

        await host.StartCorpusAsync(Local(), CancellationToken.None);

        using (var lease = host.TryLease())
        {
            await lease!.Corpus.Store.CreateCollectionAsync("site-docs", dimension: 2, distance: "cosine");
        }

        var on = await host.StateAsync("node-1", CancellationToken.None);

        Assert.True(on.Enabled);
        Assert.Equal(InferHub.Shared.Contracts.NodeCorpusState.Running, on.Status);
        Assert.Equal("local", on.Provider);
        Assert.Contains(on.Collections, collection => collection.Name == "site-docs");
        Assert.Null(on.Error);
    }

    private RetrievalHost Host(Action<LocalRetrievalOptions>? configure = null)
    {
        var options = new LocalRetrievalOptions
        {
            DataDirectory = root,
            DefaultEmbeddingModel = "test-embed"
        };

        configure?.Invoke(options);

        // The corpus needs an embedding path only when something asks it to embed; these tests drive
        // the store and the lifecycle, so the service provider carries just what construction reads.
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton<IEmbeddingDispatcher, NoEmbeddings>();
        services.AddSingleton<IReranker, NoReranker>();
        services.AddSingleton<IRetrievalMetrics>(NullRetrievalMetrics.Instance);
        services.AddSingleton<IVectorQueryRouter, NullVectorQueryRouter>();
        services.AddSingleton(_ => new InferHub.Shared.Ingestion.TextExtractor());

        var host = new RetrievalHost(
            services.BuildServiceProvider(),
            Options.Create(options),
            NullLogger<RetrievalHost>.Instance);

        hosts.Add(host);
        return host;
    }

    private static CorpusRequest Local(string? embeddingModel = null) =>
        new("local", null, null, null, embeddingModel, CorpusRequest.Profile);

    /// <summary>These tests drive the lifecycle, not the corpus; nothing here asks for a vector.</summary>
    private sealed class NoEmbeddings : IEmbeddingDispatcher
    {
        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoReranker : IReranker
    {
        public Task<IReadOnlyList<VectorMatch>> RerankAsync(
            string query,
            IReadOnlyList<VectorMatch> candidates,
            string? model,
            CancellationToken cancellationToken) => Task.FromResult(candidates);
    }
}
