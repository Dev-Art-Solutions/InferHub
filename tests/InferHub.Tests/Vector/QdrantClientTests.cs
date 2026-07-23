using System.Net;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Vector.Qdrant;
using InferHub.Shared.Vector.Storage;

namespace InferHub.Tests.Vector;

/// <summary>
/// The hand-rolled Qdrant REST client, against a stub handler. Every assertion is about the wire:
/// the exact JSON the connector sends and how it reads the answers back. No server, no dependency —
/// which is the whole point of writing the client by hand.
/// </summary>
public class QdrantClientTests
{
    [Fact]
    public async Task CreateCollectionSendsVectorParamsAndHnswConfig()
    {
        var stub = RecordingHandler.Ok("""{"result":true,"status":"ok"}""");
        var client = Client(stub);

        await client.CreateCollectionAsync("inferhub_docs", 768, DistanceMetric.Cosine, hnswM: 16, hnswEfConstruct: 64, CancellationToken.None);

        Assert.Equal(HttpMethod.Put, stub.LastMethod);
        Assert.Equal("/collections/inferhub_docs", stub.LastPath);
        var body = stub.LastBody();
        Assert.Equal(768, body.GetProperty("vectors").GetProperty("size").GetInt32());
        Assert.Equal("Cosine", body.GetProperty("vectors").GetProperty("distance").GetString());
        Assert.Equal(16, body.GetProperty("hnsw_config").GetProperty("m").GetInt32());
        Assert.Equal(64, body.GetProperty("hnsw_config").GetProperty("ef_construct").GetInt32());
    }

    [Fact]
    public async Task SearchSendsPayloadFlagsAndEfParam()
    {
        var stub = RecordingHandler.Ok("""{"result":[{"score":0.91,"payload":{"__id":"a"}}]}""");
        var client = Client(stub);

        var filter = new QdrantFilter([new QdrantFieldCondition("__meta.documentId", new QdrantMatch("handbook"))]);
        var results = await client.SearchAsync("inferhub_docs", [1f, 0f], limit: 5, filter, efSearch: 40, CancellationToken.None);

        Assert.Equal("/collections/inferhub_docs/points/search", stub.LastPath);
        var body = stub.LastBody();
        Assert.Equal(5, body.GetProperty("limit").GetInt32());
        Assert.True(body.GetProperty("with_payload").GetBoolean());
        Assert.False(body.GetProperty("with_vector").GetBoolean());
        Assert.Equal(40, body.GetProperty("params").GetProperty("hnsw_ef").GetInt32());
        Assert.Equal("handbook", body.GetProperty("filter").GetProperty("must")[0].GetProperty("match").GetProperty("value").GetString());

        Assert.Single(results);
        Assert.Equal(0.91, results[0].Score, 3);
    }

    [Fact]
    public async Task ScrollSendsOffsetAndSuppressesVectors()
    {
        var stub = RecordingHandler.Ok("""{"result":{"points":[],"next_page_offset":null}}""");
        var client = Client(stub);

        using var offsetDoc = JsonDocument.Parse("\"11111111-1111-5111-8111-111111111111\"");
        await client.ScrollAsync("inferhub_docs", filter: null, limit: 256, offsetDoc.RootElement, withVector: false, CancellationToken.None);

        Assert.Equal("/collections/inferhub_docs/points/scroll", stub.LastPath);
        var body = stub.LastBody();
        Assert.False(body.GetProperty("with_vector").GetBoolean());
        Assert.Equal("11111111-1111-5111-8111-111111111111", body.GetProperty("offset").GetString());
    }

    [Fact]
    public async Task DeleteByFilterSendsTheFilter()
    {
        var stub = RecordingHandler.Ok("""{"result":{"status":"completed"}}""");
        var client = Client(stub);

        var filter = new QdrantFilter([new QdrantFieldCondition("__meta.documentId", new QdrantMatch("gone"))]);
        await client.DeleteByFilterAsync("inferhub_docs", filter, CancellationToken.None);

        Assert.Equal("/collections/inferhub_docs/points/delete", stub.LastPath);
        Assert.Equal("gone", stub.LastBody().GetProperty("filter").GetProperty("must")[0].GetProperty("match").GetProperty("value").GetString());
    }

    [Fact]
    public async Task CountReadsTheExactCount()
    {
        var stub = RecordingHandler.Ok("""{"result":{"count":3}}""");
        var client = Client(stub);

        var count = await client.CountAsync("inferhub_docs", filter: null, CancellationToken.None);

        Assert.Equal(3, count);
        Assert.True(stub.LastBody().GetProperty("exact").GetBoolean());
    }

    [Fact]
    public async Task GetCollectionReturnsNullOnNotFound()
    {
        var stub = RecordingHandler.Status(HttpStatusCode.NotFound, """{"status":{"error":"Not found"}}""");
        var client = Client(stub);

        var result = await client.GetCollectionAsync("inferhub_missing", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ApiKeyIsSentAsHeader()
    {
        var stub = RecordingHandler.Ok("""{"result":{"collections":[]}}""");
        var http = QdrantClient.Configure(new HttpClient(stub), "http://localhost:6333", "secret-key", 30);
        var client = new QdrantClient(http);

        await client.ListCollectionNamesAsync(CancellationToken.None);

        Assert.Equal("secret-key", stub.LastApiKey);
    }

    [Fact]
    public async Task NonSuccessBecomesQdrantExceptionCarryingStatus()
    {
        var stub = RecordingHandler.Status(HttpStatusCode.BadRequest, """{"status":{"error":"Wrong vector size"}}""");
        var client = Client(stub);

        var ex = await Assert.ThrowsAsync<QdrantException>(() =>
            client.CreateCollectionAsync("inferhub_docs", 3, DistanceMetric.Cosine, 16, 64, CancellationToken.None));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Wrong vector size", ex.Message);
    }

    private static QdrantClient Client(RecordingHandler stub)
        => new(QdrantClient.Configure(new HttpClient(stub), "http://localhost:6333", null, 30));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        private string? _requestBody;

        public string? LastPath { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastApiKey { get; private set; }

        public static RecordingHandler Ok(string body) => new() { _body = body };
        public static RecordingHandler Status(HttpStatusCode status, string body) => new() { _status = status, _body = body };

        public JsonElement LastBody() => JsonDocument.Parse(_requestBody ?? "{}").RootElement.Clone();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastMethod = request.Method;
            LastApiKey = request.Headers.TryGetValues("api-key", out var values) ? values.FirstOrDefault() : null;
            if (request.Content is not null)
            {
                _requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
