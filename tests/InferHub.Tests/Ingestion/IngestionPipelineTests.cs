using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Ingestion;
using InferHub.Coordinator.Observability;
using InferHub.Coordinator.Services;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests.Ingestion;

public class IngestionPipelineTests
{
    private const string Collection = "docs";

    [Fact]
    public async Task UploadProducesChunksThatAreRetrievable()
    {
        var h = await NewHarnessAsync(maxChars: 100, overlap: 0);
        var text = string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"Paragraph {i} of the employee handbook."));

        var result = await h.Ingest(text, documentId: "handbook");

        Assert.Equal(IngestResult.Ingested, result.Status);
        Assert.True(result.Chunks > 1);
        Assert.Equal(result.Chunks, result.ChunksEmbedded);

        var info = await h.Store.GetCollectionAsync(Collection);
        Assert.Equal(result.Chunks, info!.RecordCount);

        var matches = await h.Store.QueryAsync(Collection, new VectorQuery(Vector: [1f, 0f], K: 3));
        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.Equal("handbook", m.Metadata![ChunkMetadata.DocumentId]));
    }

    [Fact]
    public async Task ChunkTextAndProvenanceLandInThePayload()
    {
        var h = await NewHarnessAsync();

        await h.Ingest("The leave policy grants 25 days.", documentId: "policy");

        var record = await h.Store.GetAsync(Collection, DocumentIndex.ChunkId("policy", 0));
        var payload = record!.Payload!.Value;

        Assert.Equal("The leave policy grants 25 days.", payload.GetProperty("text").GetString());
        Assert.Equal("policy", payload.GetProperty("documentId").GetString());
        Assert.Equal(0, payload.GetProperty("chunkIndex").GetInt32());
    }

    [Fact]
    public async Task IdenticalBytesAreANoOpAndSaySo()
    {
        var h = await NewHarnessAsync();
        const string text = "The same bytes, twice.";

        var first = await h.Ingest(text, documentId: "twice");
        var embedCallsAfterFirst = h.Embeddings.Calls;

        var second = await h.Ingest(text, documentId: "twice");

        Assert.Equal(IngestResult.Ingested, first.Status);
        Assert.Equal(IngestResult.Unchanged, second.Status);
        Assert.Equal(0, second.ChunksEmbedded);
        Assert.Equal(embedCallsAfterFirst, h.Embeddings.Calls); // not one further embed
    }

    [Fact]
    public async Task ReIngestReplacesChunksRatherThanAppending()
    {
        var h = await NewHarnessAsync();

        await h.Ingest("Version one of the text.", documentId: "doc");
        await h.Ingest("Version two of the text, revised.", documentId: "doc");

        var info = await h.Store.GetCollectionAsync(Collection);
        Assert.Equal(1, info!.RecordCount); // replaced in place, not layered underneath

        var record = await h.Store.GetAsync(Collection, DocumentIndex.ChunkId("doc", 0));
        Assert.Equal("Version two of the text, revised.", record!.Payload!.Value.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AShorterRevisionLeavesNoOrphanedTailChunks()
    {
        // Deterministic ids make re-ingest idempotent; they do not make a document *shrink*. The
        // chunks past the new end were never overwritten, because those indices no longer exist —
        // and a stale tail retrieves as confidently as a live chunk.
        var h = await NewHarnessAsync(maxChars: 100, overlap: 0);
        var longText = string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"Paragraph {i} with filler."));

        var first = await h.Ingest(longText, documentId: "shrinking");
        Assert.True(first.Chunks >= 3);

        var second = await h.Ingest("Now it is one line.", documentId: "shrinking");

        Assert.Equal(1, second.Chunks);
        var info = await h.Store.GetCollectionAsync(Collection);
        Assert.Equal(1, info!.RecordCount);
        Assert.Null(await h.Store.GetAsync(Collection, DocumentIndex.ChunkId("shrinking", 1)));
    }

    [Fact]
    public async Task DeleteRemovesEveryChunkOfThatDocumentAndNoOthers()
    {
        var h = await NewHarnessAsync(maxChars: 100, overlap: 0);
        var many = string.Join("\n\n", Enumerable.Range(0, 8).Select(i => $"Paragraph {i} with filler."));

        var doomed = await h.Ingest(many, documentId: "doomed");
        await h.Ingest("A survivor.", documentId: "keeper");

        var deleted = await h.Documents.DeleteAsync(Collection, "doomed", CancellationToken.None);

        Assert.Equal(doomed.Chunks, deleted);
        Assert.Null(await h.Documents.GetAsync(Collection, "doomed", CancellationToken.None));
        Assert.NotNull(await h.Documents.GetAsync(Collection, "keeper", CancellationToken.None));

        var info = await h.Store.GetCollectionAsync(Collection);
        Assert.Equal(1, info!.RecordCount);
    }

    [Fact]
    public async Task PartialFailureIsReportedHonestlyAndTheDocumentSaysPartial()
    {
        var h = await NewHarnessAsync(maxChars: 60, overlap: 0, batchSize: 2);
        var text = string.Join("\n\n", Enumerable.Range(0, 10).Select(i => $"Paragraph {i} filler."));

        h.Embeddings.FailFromCall = 5; // the third batch onward cannot embed

        var result = await h.Ingest(text, documentId: "flaky");

        Assert.Equal(IngestResult.Partial, result.Status);
        Assert.NotNull(result.Error);
        Assert.True(result.ChunksEmbedded > 0);
        Assert.True(result.ChunksEmbedded < result.Chunks);

        var document = await h.Documents.GetAsync(Collection, "flaky", CancellationToken.None);
        Assert.Equal(IngestResult.Partial, document!.Status);
        Assert.Equal(result.ChunksEmbedded, document.Chunks);
    }

    [Fact]
    public async Task APartialDocumentIsRetriedRatherThanReportedUnchanged()
    {
        // Same bytes, same hash — but last time they did not all land. Answering "unchanged" here
        // would be a lie about a document that is half-missing.
        var h = await NewHarnessAsync(maxChars: 60, overlap: 0, batchSize: 2);
        var text = string.Join("\n\n", Enumerable.Range(0, 8).Select(i => $"Paragraph {i} filler."));

        h.Embeddings.FailFromCall = 3;
        var partial = await h.Ingest(text, documentId: "retry");
        Assert.Equal(IngestResult.Partial, partial.Status);

        h.Embeddings.FailFromCall = null;
        var retried = await h.Ingest(text, documentId: "retry");

        Assert.Equal(IngestResult.Ingested, retried.Status);
        Assert.Equal(retried.Chunks, retried.ChunksEmbedded);

        var document = await h.Documents.GetAsync(Collection, "retry", CancellationToken.None);
        Assert.Equal("complete", document!.Status);
    }

    [Fact]
    public async Task ATransientEmbeddingFailureIsRetriedWithinTheBatch()
    {
        var h = await NewHarnessAsync();
        h.Embeddings.FailOnce = true;

        var result = await h.Ingest("One paragraph.", documentId: "flaky-once");

        Assert.Equal(IngestResult.Ingested, result.Status);
    }

    [Fact]
    public async Task NoNodeAdvertisingTheModelIsNotRetried()
    {
        // "No node has this model" will not fix itself in 400 ms. Retrying it three times just
        // makes the caller wait longer for the same answer.
        var h = await NewHarnessAsync();
        h.Embeddings.NoNode = true;

        var result = await h.Ingest("Anything.", documentId: "nowhere");

        Assert.Equal(IngestResult.Partial, result.Status);
        Assert.Equal(0, result.ChunksEmbedded);
        Assert.Equal(1, h.Embeddings.Calls); // one attempt, not MaxRetriesPerBatch
    }

    [Fact]
    public async Task IngestingIntoAMissingCollectionFailsBeforeAnyEmbedding()
    {
        var h = await NewHarnessAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Pipeline.IngestAsync("ghost", new IngestRequest(Encoding.UTF8.GetBytes("hi")), CancellationToken.None));

        Assert.Equal(0, h.Embeddings.Calls);
    }

    [Fact]
    public async Task AnOversizedDocumentIsRejectedBeforeAnyWork()
    {
        var h = await NewHarnessAsync(maxBytes: 32);

        await Assert.ThrowsAsync<DocumentTooLargeException>(
            () => h.Ingest(new string('x', 200), documentId: "huge"));

        Assert.Equal(0, h.Embeddings.Calls);
    }

    [Fact]
    public async Task CallerMetadataSurvivesButCannotShadowTheDocumentModel()
    {
        var h = await NewHarnessAsync();

        await h.Pipeline.IngestAsync(Collection, new IngestRequest(
            Content: Encoding.UTF8.GetBytes("Some text."),
            DocumentId: "real",
            ContentType: "text/plain",
            Metadata: new Dictionary<string, string> { ["team"] = "hr", [ChunkMetadata.DocumentId] = "spoofed" }),
            CancellationToken.None);

        var record = await h.Store.GetAsync(Collection, DocumentIndex.ChunkId("real", 0));

        Assert.Equal("hr", record!.Metadata!["team"]);
        Assert.Equal("real", record.Metadata[ChunkMetadata.DocumentId]);
    }

    [Fact]
    public async Task DocumentsAreListedWithChunkCountsAndProvenance()
    {
        var h = await NewHarnessAsync(maxChars: 100, overlap: 0);

        await h.Pipeline.IngestAsync(Collection, new IngestRequest(
            Content: Encoding.UTF8.GetBytes(string.Join("\n\n", Enumerable.Range(0, 6).Select(i => $"Paragraph {i} filler."))),
            FileName: "handbook.md"),
            CancellationToken.None);
        await h.Ingest("Second document.", documentId: "second");

        var list = await h.Documents.ListAsync(Collection, CancellationToken.None);

        Assert.Equal(2, list.Count);

        var handbook = Assert.Single(list, d => d.Id == "handbook.md"); // id derived from the filename
        Assert.Equal("handbook.md", handbook.Source);
        Assert.Equal("text/markdown", handbook.MediaType);
        Assert.Equal("complete", handbook.Status);
        Assert.True(handbook.Chunks > 1);
        Assert.True(handbook.Bytes > 0);
        Assert.NotNull(handbook.IngestedAt);
    }

    [Fact]
    public async Task PdfPagesAreCarriedThroughToChunkMetadataForCitation()
    {
        var h = await NewHarnessAsync(pdf: new StubPdf());

        await h.Pipeline.IngestAsync(Collection, new IngestRequest(
            Content: [1, 2, 3],
            DocumentId: "manual",
            FileName: "manual.pdf"),
            CancellationToken.None);

        var chunks = await h.Documents.ChunksOfAsync(Collection, "manual", CancellationToken.None);

        Assert.Equal(2, chunks.Count);
        Assert.Contains(chunks, c => c.Metadata![ChunkMetadata.Page] == "1");
        Assert.Contains(chunks, c => c.Metadata![ChunkMetadata.Page] == "7");

        var page7 = Assert.Single(chunks, c => c.Metadata![ChunkMetadata.Page] == "7");
        Assert.Equal(7, page7.Payload!.Value.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task MetricsCountDocumentsChunksAndFailures()
    {
        var h = await NewHarnessAsync(maxChars: 60, overlap: 0, batchSize: 2);

        var ok = await h.Ingest(string.Join("\n\n", Enumerable.Range(0, 4).Select(i => $"Paragraph {i} filler.")), documentId: "ok");

        h.Embeddings.FailFromCall = h.Embeddings.Calls + 1;
        await h.Ingest("Doomed text.", documentId: "bad");

        var snapshot = h.Metrics.GetVectorCollectionSnapshot(Collection);

        Assert.Equal(1, snapshot.DocumentsIngested);           // only the one that completed
        Assert.Equal(ok.ChunksEmbedded, snapshot.ChunksEmbedded);
        Assert.Equal(1, snapshot.IngestionFailures);
        Assert.Equal("test-embed", snapshot.LastEmbeddingModel);
        Assert.NotNull(snapshot.LastIngestAtUtc);
    }

    [Fact]
    public async Task AMissingCollectionIsStillARefusalWhenAutoProvisionIsOff()
    {
        // Phase 23's contract, unchanged for every unscoped client: collections are an admin's
        // to create, and ingesting into one that isn't there fails before any work is done.
        var h = await NewHarnessAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Ingest("Some text.", documentId: "doc", collection: "not-there"));

        Assert.Equal(0, h.Embeddings.Calls);
    }

    [Fact]
    public async Task AutoProvisionCreatesTheCollectionWithTheMeasuredDimension()
    {
        // Phase 31. The dimension is not guessed from config — it is whatever the model that just
        // embedded the first batch produced, which is why creation happens after the batch, not
        // before it.
        var h = await NewHarnessAsync();

        var result = await h.Ingest("The leave policy grants 25 days.", documentId: "policy",
            collection: "tenant-a-docs", autoProvision: true);

        Assert.Equal(IngestResult.Ingested, result.Status);

        var info = await h.Store.GetCollectionAsync("tenant-a-docs");
        Assert.NotNull(info);
        Assert.Equal(2, info!.Dimension);
        Assert.Equal(result.Chunks, info.RecordCount);
    }

    [Fact]
    public async Task AutoProvisionOnAnExistingCollectionJustIngests()
    {
        var h = await NewHarnessAsync();

        await h.Ingest("First document.", documentId: "one", autoProvision: true);
        await h.Ingest("Second document.", documentId: "two", autoProvision: true);

        var documents = await h.Documents.ListAsync(Collection, CancellationToken.None);
        Assert.Equal(2, documents.Count);
    }

    [Fact]
    public async Task AFailedFirstBatchLeavesNoHalfCreatedCollection()
    {
        // Creation happens only once vectors are in hand, so an embed that never succeeds cannot
        // leave an empty collection behind for the next caller to find and misread as provisioned.
        var h = await NewHarnessAsync();
        h.Embeddings.NoNode = true;

        var result = await h.Ingest("Doomed text.", documentId: "doc",
            collection: "tenant-a-docs", autoProvision: true);

        Assert.Equal(IngestResult.Partial, result.Status);
        Assert.Null(await h.Store.GetCollectionAsync("tenant-a-docs"));
    }

    private static async Task<Harness> NewHarnessAsync(
        int maxChars = 1200,
        int overlap = 150,
        int batchSize = 16,
        long maxBytes = 25 * 1024 * 1024,
        IPdfTextExtractor? pdf = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferhub-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var vectorOptions = Options.Create(new VectorStoreOptions
        {
            Enabled = true,
            DataDirectory = dir,
            Distance = "cosine",
            DefaultEmbeddingModel = "test-embed"
        });
        var ingestionOptions = Options.Create(new IngestionOptions
        {
            MaxChars = maxChars,
            OverlapChars = overlap,
            EmbeddingBatchSize = batchSize,
            MaxDocumentBytes = maxBytes,
            MaxRetriesPerBatch = 3
        });

        var store = new LocalVectorStore(vectorOptions, NullLogger<LocalVectorStore>.Instance);
        await store.CreateCollectionAsync(Collection, dimension: 2, distance: "cosine");

        var documents = new DocumentIndex(store);
        var embeddings = new FakeEmbeddings();
        var metrics = new Metrics();
        var pipeline = new IngestionPipeline(
            store, documents, new TextExtractor(pdf), embeddings,
            ingestionOptions, vectorOptions, metrics,
            NullLogger<IngestionPipeline>.Instance);

        return new Harness(pipeline, store, documents, embeddings, metrics);
    }

    private sealed record Harness(
        IngestionPipeline Pipeline,
        LocalVectorStore Store,
        DocumentIndex Documents,
        FakeEmbeddings Embeddings,
        Metrics Metrics)
    {
        public Task<IngestResult> Ingest(
            string text,
            string documentId,
            string? collection = null,
            bool autoProvision = false) =>
            Pipeline.IngestAsync(collection ?? Collection, new IngestRequest(
                Content: Encoding.UTF8.GetBytes(text),
                DocumentId: documentId,
                ContentType: "text/plain"),
                autoProvision,
                CancellationToken.None);
    }

    private sealed class FakeEmbeddings : IEmbeddingDispatcher
    {
        private int _calls;

        public int Calls => _calls;

        /// <summary>Fail every call from this call number onward — a node that went away and stayed away.</summary>
        public int? FailFromCall { get; set; }

        /// <summary>Fail exactly once, then recover — the case the in-batch retry exists for.</summary>
        public bool FailOnce { get; set; }

        public bool NoNode { get; set; }

        public Task<string> DispatchEmbedAsync(string rawJson, string? modelOverride, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<float[]> EmbedSingleAsync(string text, string? model, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            if (NoNode) throw new NoEmbeddingNodeException(model ?? "?");

            if (FailOnce)
            {
                FailOnce = false;
                throw new InvalidOperationException("node dropped the job");
            }

            if (FailFromCall is { } threshold && call >= threshold)
            {
                throw new InvalidOperationException("node dropped the job");
            }

            // Deterministic, unit-length-ish, and dependent on the text so different chunks differ.
            var hash = text.Aggregate(0, (acc, c) => acc + c);
            return Task.FromResult(new[] { 1f, hash % 7 / 10f });
        }
    }

    private sealed class StubPdf : IPdfTextExtractor
    {
        public ExtractedDocument Extract(byte[] content) => new(TextExtractor.Pdf,
        [
            new ExtractedPage(1, "Text lifted from the first page."),
            new ExtractedPage(7, "Text lifted from page seven.")
        ]);
    }
}
