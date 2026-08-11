using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;
using InferHub.Tests.Vector;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 44, D1 and D5: <b>one authority per collection name, and the hub knows who it is.</b>
/// </summary>
/// <remarks>
/// Every test here is a way that sentence could stop being true — the hub creating a name a node
/// owns, replication pushing an empty snapshot over somebody's corpus, a meshed node quietly running
/// a second authority of its own, or an ingest landing in the wrong store.
/// </remarks>
public class NodeOwnedCollectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-owned-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> disposables = new();

    public void Dispose()
    {
        foreach (var disposable in disposables)
        {
            try { disposable.Dispose(); } catch { }
        }

        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    // ---- D1: the ownership record itself ----------------------------------------------------

    [Fact]
    public void EverythingIsHubOwnedUntilSomethingIsAssigned()
    {
        var ownership = new CollectionOwnership();

        Assert.True(ownership.IsHubOwned("docs"));
        Assert.Equal(CollectionOwnership.Hub, ownership.OwnerOfCollection("docs"));
        Assert.Null(ownership.NodeOwning("docs"));
    }

    [Fact]
    public void AssigningIsDesiredStateRatherThanAnAccumulation()
    {
        var ownership = new CollectionOwnership();

        ownership.Assign("edge-1", ["a", "b"]);
        Assert.Equal("node:edge-1", ownership.OwnerOfCollection("b"));

        // The second write says "a, and nothing else" — b goes back to being nobody's, the same way
        // a profile that drops a tool stops that tool (phase-43 D2).
        ownership.Assign("edge-1", ["a"]);

        Assert.False(ownership.IsHubOwned("a"));
        Assert.True(ownership.IsHubOwned("b"));
    }

    [Fact]
    public void RebuildingFromTheProfileBookForgetsWhatNoProfileSaysAnyMore()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("edge-1", ["site-docs"]);

        // A node whose profile was deleted, and one whose retrieval was switched off: neither owns
        // anything, and the hub must not be left holding a stale claim on their names.
        ownership.Rebuild(
        [
            ("edge-1", null),
            ("edge-2", Profile(new RetrievalProfile(Enabled: false, Collections: ["other"])))
        ]);

        Assert.True(ownership.IsHubOwned("site-docs"));
        Assert.True(ownership.IsHubOwned("other"));
    }

    [Fact]
    public void ARefusalNamesTheOwnerRatherThanJustSayingConflict()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("sofia-1", ["site-sofia-docs"]);

        var refusal = ownership.RefusalFor("site-sofia-docs");

        // "409" alone sends somebody looking for a collection that is not missing.
        Assert.Contains("node:sofia-1", refusal);
        Assert.Contains("site-sofia-docs", refusal);
    }

    // ---- D1: replication and healing never target a node-owned collection --------------------

    [Fact]
    public async Task ReplicationNeverPlacesANodeOwnedCollection()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("edge-1", ["site-docs"]);

        var registry = new NodeRegistry();
        registry.Upsert(
            "conn-a",
            new NodeRegistration("node-a", "node-a", "http://x", "test", null, null, null),
            DateTimeOffset.UtcNow);

        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = root,
            ReplicationFactor = 1
        });

        var store = new LocalVectorStore(options.Value, NullVectorLog.Instance);
        disposables.Add(store);

        var replicas = new ReplicaRegistry();
        var hub = new RecordingHubContext();
        var coordinator = new ReplicationCoordinator(
            store,
            registry,
            replicas,
            hub,
            options,
            NullLogger<ReplicationCoordinator>.Instance,
            new VectorEvents(),
            ownership);

        disposables.Add(coordinator);

        // A collection with the same name exists hub-side — which is exactly the confusion the
        // ownership record is there to resolve. Placement must still refuse it.
        await store.CreateCollectionAsync("site-docs", dimension: 2, distance: "cosine");
        await coordinator.RecomputeAsync("site-docs");

        Assert.Empty(replicas.Holders("site-docs"));

        // And an ordinary collection on the same hub is replicated exactly as before, so the filter
        // is a filter and not an off switch.
        await store.CreateCollectionAsync("hub-docs", dimension: 2, distance: "cosine");
        await coordinator.RecomputeAsync("hub-docs");

        Assert.Single(replicas.Holders("hub-docs"));
    }

    [Fact]
    public async Task HealingSkipsANodeOwnedCollectionAndDoesNotCallItUnderReplicated()
    {
        var ownership = new CollectionOwnership();
        ownership.Assign("edge-1", ["site-docs"]);

        var registry = new NodeRegistry();
        registry.Upsert(
            "conn-a",
            new NodeRegistration("node-a", "node-a", "http://x", "test", null, null, null),
            DateTimeOffset.UtcNow);

        var options = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = root,
            ReplicationFactor = 1
        });

        var store = new LocalVectorStore(options.Value, NullVectorLog.Instance);
        disposables.Add(store);

        var replicas = new ReplicaRegistry();
        var coordinator = new ReplicationCoordinator(
            store,
            registry,
            replicas,
            new RecordingHubContext(),
            options,
            NullLogger<ReplicationCoordinator>.Instance,
            new VectorEvents(),
            ownership);

        disposables.Add(coordinator);

        var healing = new HealingService(
            store,
            registry,
            replicas,
            coordinator,
            options,
            new Metrics(),
            NullLogger<HealingService>.Instance,
            new VectorEvents(),
            ownership);

        disposables.Add(healing);

        await store.CreateCollectionAsync("site-docs", dimension: 2, distance: "cosine");
        await healing.HealNowAsync();

        Assert.Empty(replicas.Holders("site-docs"));

        // Asking for it by hand is refused with the owner's name, not silently obeyed.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => healing.RebuildAsync("site-docs"));
        Assert.Contains("node:edge-1", refusal.Message);
    }

    // ---- D1: the hub refuses to create a name a node owns ------------------------------------

    [Fact]
    public async Task AHubSideCreateOfANodeOwnedNameIsA409NamingTheOwner()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);
        hub.Ownership.Assign("node-1", ["site-docs"]);

        var response = await hub.Client.PostAsync(
            "/api/admin/vector/collections",
            JsonBody(new { name = "site-docs", dimension = 3 }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("node:node-1", body);
    }

    // ---- phase-38 D1 is unchanged: a self-configured corpus on a meshed node still fails ------

    /// <summary>
    /// The amendment in phase-44 D1 is narrow, and this is the line it does not cross: the hub may
    /// <em>grant</em> a corpus and record who owns it, but a node that switches
    /// <c>LocalApi:Retrieval:Enabled</c> on for itself while meshed is still a startup failure. That
    /// node would hold hub-derived replicas and its own authority under the same names, with nothing
    /// anywhere recording the fact.
    /// </summary>
    [Fact]
    public void ASelfConfiguredCorpusOnAMeshedNodeStillFailsAtStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:Enabled"] = "true",
                ["LocalApi:Enabled"] = "true"
            })
            .Build();

        var result = new LocalRetrievalOptionsValidator(configuration).Validate(
            null,
            new LocalRetrievalOptions { Enabled = true });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Coordinator:Enabled"));
    }

    // ---- D5: work for a node-owned collection goes to its owner ------------------------------

    [Fact]
    public async Task IngestionForANodeOwnedCollectionIsDispatchedToTheOwner()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);
        hub.Ownership.Assign("node-1", ["site-docs"]);

        hub.Dispatcher.CorpusResponse = JsonSerializer.Serialize(new
        {
            result = new
            {
                collection = "site-docs",
                documentId = "d1",
                status = IngestResult.Ingested,
                chunks = 1,
                chunksEmbedded = 1,
                bytes = 21,
                contentHash = "abc"
            }
        });

        var response = await hub.Client.PostAsync(
            "/api/collections/site-docs/documents",
            JsonBody(new { id = "d1", text = "a node-owned document" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The job went to the owner, as a corpus job — not to the hub's own pipeline, and not as an
        // inference job with a foreign body in it (phase-40 D3).
        Assert.Equal(CorpusJobKinds.Ingest, hub.Dispatcher.LastJobKind);
        Assert.Equal("node-1", hub.Dispatcher.LastNodeId);

        var job = JsonSerializer.Deserialize<JsonElement>(hub.Dispatcher.LastJobJson!);
        Assert.Equal("site-docs", job.GetProperty("collection").GetString());

        // And the hub's own store never saw it.
        Assert.Null(await hub.StoreCollectionAsync("site-docs"));
    }

    [Fact]
    public async Task PdfIsA415ForANodeOwnedCollectionRatherThanASecondIngestionPath()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);
        hub.Ownership.Assign("node-1", ["site-docs"]);

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.4 not really"));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "handbook.pdf");

        var response = await hub.Client.PostAsync("/api/collections/site-docs/documents", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("node-owned collection", await response.Content.ReadAsStringAsync());

        // Nothing was dispatched: the refusal happens before the document leaves the hub.
        Assert.NotEqual(CorpusJobKinds.Ingest, hub.Dispatcher.LastJobKind);
    }

    [Fact]
    public async Task SearchOfANodeOwnedCollectionRunsOnTheOwner()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);
        hub.Ownership.Assign("node-1", ["site-docs"]);

        hub.Dispatcher.CorpusResponse = JsonSerializer.Serialize(new
        {
            matches = new[]
            {
                new
                {
                    id = "chunk-1",
                    score = 0.9,
                    payload = JsonDocument.Parse("""{"text":"the answer"}""").RootElement,
                    metadata = new Dictionary<string, string> { ["documentId"] = "d1" }
                }
            }
        });

        var response = await hub.Client.PostAsync(
            "/api/collections/site-docs/search",
            JsonBody(new { query = "what is the answer" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CorpusJobKinds.Search, hub.Dispatcher.LastJobKind);
        Assert.Equal("node-1", hub.Dispatcher.LastNodeId);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chunk-1", body.GetProperty("hits")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnOwnerThatIsNotConnectedIsA503AndNeverTheHubsOwnCorpus()
    {
        await using var hub = await HubHost.StartAsync(ChatResponse, retrieval: true);

        // A node that is not in the registry — rebooting, or behind a router that dropped the link.
        hub.Ownership.Assign("node-that-is-away", ["site-docs"]);

        // The hub happens to hold a collection of the same name, which is exactly the case where
        // answering from it would be a confident answer from the wrong data (phase-31 D4).
        await hub.CreateCollectionAsync("site-docs", TestEmbeddings.Dimension);

        var response = await hub.Client.PostAsync(
            "/api/collections/site-docs/search",
            JsonBody(new { query = "anything" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("node-that-is-away", await response.Content.ReadAsStringAsync());
    }

    private static NodeProfile Profile(RetrievalProfile retrieval) => new(
        "p",
        1,
        new NodeProfileSelector(NodeId: "edge-2"),
        Retrieval: retrieval);

    private const string ChatResponse =
        """{"model":"llama3","message":{"role":"assistant","content":"ok"},"done":true}""";

    private static HttpContent JsonBody(object body) =>
        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
