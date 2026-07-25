using InferHub.Coordinator.Vector;
using InferHub.Shared.Vector;

namespace InferHub.Migrate;

public sealed record MigrationOptions(
    string? Collection = null,
    int BatchSize = 256,
    int Parallelism = 4,
    bool DryRun = false);

/// <summary>What happened to one collection. <see cref="Skipped"/> is non-null when nothing was copied and why.</summary>
public sealed record CollectionResult(
    string Name,
    int Dimension,
    string Distance,
    long SourceCount,
    long Copied,
    long TargetCount,
    bool TargetCreated,
    string? Skipped = null);

/// <summary>
/// Copies collections from one <see cref="IVectorStore"/> to another. Both sides are just the seam,
/// so every provider pair works and none of them is special-cased — which is also why this is the
/// class the tests drive directly, with two local stores, rather than only through the CLI.
/// <para>
/// A re-run is safe. Ids are the caller's own (phase-23 D5 makes a chunk id deterministic), and an
/// upsert of an id that is already there replaces it, so copying the same collection twice converges
/// rather than duplicating. It does <b>not</b> delete: a record in the target that is not in the
/// source is left alone, because a migration tool that removes data nobody asked it to remove is a
/// worse failure than one that leaves a stale record behind.
/// </para>
/// </summary>
public sealed class Migrator(IVectorStore source, IVectorStore target, Action<string>? report = null)
{
    private readonly Action<string> _report = report ?? (_ => { });

    public async Task<IReadOnlyList<CollectionResult>> RunAsync(MigrationOptions options, CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Max(1, options.BatchSize);
        var parallelism = Math.Max(1, options.Parallelism);

        var results = new List<CollectionResult>();
        foreach (var info in await ResolveCollectionsAsync(options.Collection, cancellationToken))
        {
            results.Add(await CopyCollectionAsync(info, batchSize, parallelism, options.DryRun, cancellationToken));
        }
        return results;
    }

    private async Task<IReadOnlyList<CollectionInfo>> ResolveCollectionsAsync(string? only, CancellationToken cancellationToken)
    {
        if (only is null)
        {
            return await source.ListCollectionsAsync(cancellationToken);
        }

        var info = await source.GetCollectionAsync(only, cancellationToken)
            ?? throw new InvalidOperationException($"collection '{only}' does not exist in the source store.");
        return [info];
    }

    private async Task<CollectionResult> CopyCollectionAsync(
        CollectionInfo info, int batchSize, int parallelism, bool dryRun, CancellationToken cancellationToken)
    {
        var existing = await target.GetCollectionAsync(info.Name, cancellationToken);

        // A target collection whose shape disagrees with the source is not something to paper over:
        // upserting 768-float vectors into a 384 collection fails per record, and upserting into a
        // collection with a different distance would succeed and silently rank differently. Say why
        // and move to the next collection rather than half-copying this one.
        if (existing is not null && existing.Dimension != info.Dimension)
        {
            return Skip(info, $"target already has '{info.Name}' with dimension {existing.Dimension}, source is {info.Dimension}");
        }
        if (existing is not null && !string.Equals(existing.Distance, info.Distance, StringComparison.OrdinalIgnoreCase))
        {
            return Skip(info, $"target already has '{info.Name}' with distance '{existing.Distance}', source is '{info.Distance}'");
        }

        var mustCreate = existing is null;

        if (dryRun)
        {
            _report($"  {info.Name}: would {(mustCreate ? "create" : "reuse")} target collection " +
                    $"(dim={info.Dimension}, distance={info.Distance}) and copy {info.RecordCount} record(s)");
            return new CollectionResult(info.Name, info.Dimension, info.Distance, info.RecordCount, 0, existing?.RecordCount ?? 0, mustCreate);
        }

        if (mustCreate)
        {
            await target.CreateCollectionAsync(info.Name, info.Dimension, info.Distance, cancellationToken);
        }

        long copied = 0;
        string? afterId = null;
        while (true)
        {
            var batch = await source.ScanWithVectorsAsync(info.Name, filter: null, batchSize, afterId, cancellationToken);
            if (batch.Count == 0) break;

            // Writes go one record at a time because that is what the seam offers; a batch upsert on
            // IVectorStore would be a method three providers carry for one caller. Bounded concurrency
            // hides the round trip instead. Order does not matter — records are independent by id.
            foreach (var slice in batch.Chunk(parallelism))
            {
                await Task.WhenAll(slice.Select(r => target.UpsertAsync(
                    info.Name, new VectorUpsert(r.Id, r.Vector, r.Payload, r.Metadata), cancellationToken)));
            }

            copied += batch.Count;
            // ScanWithVectorsAsync is ordered by id, so the last of the page is the next cursor.
            afterId = batch[^1].Id;
            _report($"  {info.Name}: {copied}/{info.RecordCount}");
        }

        var after = await target.GetCollectionAsync(info.Name, cancellationToken);
        return new CollectionResult(info.Name, info.Dimension, info.Distance, info.RecordCount, copied, after?.RecordCount ?? 0, mustCreate);
    }

    private CollectionResult Skip(CollectionInfo info, string reason)
    {
        _report($"  {info.Name}: skipped — {reason}");
        return new CollectionResult(info.Name, info.Dimension, info.Distance, info.RecordCount, 0, 0, false, reason);
    }
}
