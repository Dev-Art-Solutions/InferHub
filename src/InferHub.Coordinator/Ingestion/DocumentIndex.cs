using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;

namespace InferHub.Coordinator.Ingestion;

/// <summary>
/// The document view of a collection, derived entirely from chunk metadata. Nothing here persists
/// anything: a document *is* the set of chunks sharing a <c>documentId</c>, and this class is the
/// only thing that knows how to read that set back as a document (D1).
/// </summary>
public sealed class DocumentIndex(IVectorStore store)
{
    /// <summary>
    /// Page size for the underlying scans. Documents are assembled by walking every chunk in the
    /// collection, which is linear in chunk count — fine at InferHub's scale (a handbook is
    /// hundreds of chunks, a corpus is thousands) and the honest cost of not keeping a second index.
    /// </summary>
    private const int ScanPageSize = 500;

    /// <summary>
    /// Deterministic chunk id (D5): re-ingesting a document overwrites its chunks in place rather
    /// than layering a second copy underneath the first, and a citation minted last month still
    /// points at the same chunk today.
    /// </summary>
    public static string ChunkId(string documentId, int chunkIndex)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{documentId}:{chunkIndex}"));
        return Convert.ToHexStringLower(bytes);
    }

    public static string ContentHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(string collection, CancellationToken cancellationToken)
    {
        var chunks = await ScanAllAsync(collection, filter: null, cancellationToken);
        return chunks
            .Where(c => Meta(c, ChunkMetadata.DocumentId) is not null)
            .GroupBy(c => Meta(c, ChunkMetadata.DocumentId)!, StringComparer.Ordinal)
            .Select(g => Summarise(g.Key, g.ToList()))
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<DocumentSummary?> GetAsync(string collection, string documentId, CancellationToken cancellationToken)
    {
        var chunks = await ChunksOfAsync(collection, documentId, cancellationToken);
        return chunks.Count == 0 ? null : Summarise(documentId, chunks);
    }

    public Task<IReadOnlyList<VectorEntry>> ChunksOfAsync(string collection, string documentId, CancellationToken cancellationToken) =>
        ScanAllAsync(collection, new Dictionary<string, string> { [ChunkMetadata.DocumentId] = documentId }, cancellationToken);

    public Task<int> DeleteAsync(string collection, string documentId, CancellationToken cancellationToken) =>
        store.DeleteByFilterAsync(collection, new Dictionary<string, string> { [ChunkMetadata.DocumentId] = documentId }, cancellationToken);

    private async Task<IReadOnlyList<VectorEntry>> ScanAllAsync(
        string collection,
        IReadOnlyDictionary<string, string>? filter,
        CancellationToken cancellationToken)
    {
        var all = new List<VectorEntry>();
        string? after = null;

        while (true)
        {
            var page = await store.ScanAsync(collection, filter, ScanPageSize, after, cancellationToken);
            if (page.Count == 0) break;

            all.AddRange(page);
            if (page.Count < ScanPageSize) break;
            after = page[^1].Id;
        }

        return all;
    }

    /// <summary>
    /// A document is <c>partial</c> when fewer chunks are present than its chunks say there should
    /// be — the state a run that failed mid-embedding leaves behind. Deriving it here, from what is
    /// actually stored, means the verdict cannot go stale.
    /// </summary>
    private static DocumentSummary Summarise(string documentId, IReadOnlyList<VectorEntry> chunks)
    {
        var first = chunks[0];
        var expected = MetaInt(first, ChunkMetadata.ChunkCount) ?? chunks.Count;
        var status = chunks.Count >= expected ? "complete" : IngestResult.Partial;

        return new DocumentSummary(
            documentId,
            chunks.Count,
            MetaLong(first, ChunkMetadata.Bytes) ?? 0,
            Meta(first, ChunkMetadata.ContentHash) ?? "",
            MetaDate(first, ChunkMetadata.IngestedAt),
            Meta(first, ChunkMetadata.Source),
            Meta(first, ChunkMetadata.MediaType),
            status);
    }

    internal static string? Meta(VectorEntry entry, string key) =>
        entry.Metadata is not null && entry.Metadata.TryGetValue(key, out var value) ? value : null;

    private static int? MetaInt(VectorEntry entry, string key) =>
        int.TryParse(Meta(entry, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static long? MetaLong(VectorEntry entry, string key) =>
        long.TryParse(Meta(entry, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static DateTimeOffset? MetaDate(VectorEntry entry, string key) =>
        DateTimeOffset.TryParse(Meta(entry, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
