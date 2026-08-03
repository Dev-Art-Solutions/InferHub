using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using InferHub.Shared.Ingestion;
using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Vector;

/// <summary>
/// Sends work for a node-owned collection to the node that owns it (phase 44, D5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The client-facing API stays the hub's.</b> A client posts a document to
/// <c>/api/collections/{c}/documents</c> and searches at <c>/api/collections/{c}/search</c> exactly
/// as it always has, whether the data lives in the hub's store or on a box in another city. What
/// changes is one lookup and one dispatch, down the connection the node already opened — the hub
/// still never dials a node (phase-26 D1).
/// </para>
/// <para>
/// Client scoping (phase-31 D2) is enforced <em>before</em> this, by the same group filter as ever,
/// which is the point of keeping the API here: the hub goes on being the thing that decides who may
/// touch which collection, over data it does not hold.
/// </para>
/// </remarks>
public sealed class NodeCorpusDispatcher(
    CollectionOwnership ownership,
    INodeRegistry registry,
    IDispatcher dispatcher,
    ILogger<NodeCorpusDispatcher> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Whether this collection belongs to a node rather than to the hub.</summary>
    public bool IsNodeOwned(string collection) => !ownership.IsHubOwned(collection);

    public string? OwnerOf(string collection) => ownership.NodeOwning(collection);

    /// <summary>
    /// Ingests into a node-owned collection by handing the document to its owner. Returns the
    /// pipeline's own <see cref="IngestResult"/>, so a client cannot tell from the body which host
    /// did the work — only from where the bytes ended up.
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        string collection,
        IngestRequest request,
        CancellationToken cancellationToken)
    {
        var route = Route(collection);
        var job = new InferenceJob(
            Guid.NewGuid(),
            CorpusJobKinds.Ingest,
            JsonSerializer.Serialize(new CorpusIngestJob(collection, request), JsonOptions));

        var result = await dispatcher.DispatchAsync(route, job, cancellationToken);

        if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
        {
            throw new NodeCorpusUnavailableException(
                $"the node that owns '{collection}' could not ingest it: {result.Error ?? "no response"}");
        }

        var response = JsonSerializer.Deserialize<CorpusIngestResponse>(result.ResponseJson, JsonOptions);

        return response?.Result
            ?? throw new NodeCorpusUnavailableException($"the node that owns '{collection}' returned nothing readable");
    }

    /// <summary>Searches a node-owned collection on its owner, through the owner's shared pipeline.</summary>
    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(
        CorpusSearchJob search,
        CancellationToken cancellationToken)
    {
        var route = Route(search.Collection);
        var job = new InferenceJob(
            Guid.NewGuid(),
            CorpusJobKinds.Search,
            JsonSerializer.Serialize(search, JsonOptions));

        var result = await dispatcher.DispatchAsync(route, job, cancellationToken);

        if (!result.Success || string.IsNullOrEmpty(result.ResponseJson))
        {
            throw new NodeCorpusUnavailableException(
                $"the node that owns '{search.Collection}' could not search it: {result.Error ?? "no response"}");
        }

        var response = JsonSerializer.Deserialize<CorpusSearchResponse>(result.ResponseJson, JsonOptions);
        return response?.Matches ?? Array.Empty<VectorMatch>();
    }

    /// <summary>
    /// The owner's live connection, or a refusal that says which box is missing. <b>There is no
    /// fallback to the hub's own store</b>: answering from a different corpus because the right one
    /// is asleep is phase-31 D4's failure — a confident answer from the wrong data.
    /// </summary>
    private RoutableNode Route(string collection)
    {
        var nodeId = ownership.NodeOwning(collection)
            ?? throw new InvalidOperationException($"collection '{collection}' is not node-owned");

        var node = registry.Snapshot(DateTimeOffset.UtcNow)
            .FirstOrDefault(n => string.Equals(n.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            logger.LogWarning(
                "Collection '{Collection}' is owned by node {NodeId}, which is not connected",
                collection,
                nodeId);

            throw new NodeCorpusUnavailableException(
                $"collection '{collection}' lives on node '{nodeId}', which is not connected right now. It will answer again when that node reconnects; this hub holds no copy of it.");
        }

        return new RoutableNode(node.ConnectionId, node.NodeId, node.Name);
    }
}

/// <summary>
/// The owner of a collection is not reachable, or could not do the work. A 503 rather than a 500:
/// the request is fine and will work again when the box comes back.
/// </summary>
public sealed class NodeCorpusUnavailableException(string message) : InvalidOperationException(message);
