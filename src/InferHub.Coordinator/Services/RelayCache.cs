using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 123. <c>Relay:Cache:</c> — the blobs this hub relayed, kept on its disk by sha256 so the next sealed node's
/// pull of the same model does not cross the hub's uplink again. Off by default; rule 4's sixth exception (123 D4).
/// </summary>
public sealed class RelayCacheOptions
{
    public bool Enabled { get; set; }

    /// <summary>Relative by default so bare metal and Windows work; the image points it at <c>/data/relay</c>.</summary>
    public string DataDirectory { get; set; } = "./data/relay";

    /// <summary>The most the cache holds; the least recently served blob goes first.</summary>
    public double MaxGiB { get; set; } = 200;

    /// <summary>A blob is not kept when keeping it would leave the disk with less than this free.</summary>
    public double KeepFreeGiB { get; set; } = 20;
}

/// <summary>What the cache holds and what it has done since the hub started. Counts, never names (rule 7's habit).</summary>
public sealed record RelayCacheSnapshot(
    int Entries,
    long Bytes,
    long MaxBytes,
    long Hits,
    long Misses,
    long Filled,
    long Discarded,
    long Skipped,
    long ServedBytes);

/// <summary>A kept blob, open for reading. The stream is the caller's to dispose.</summary>
public sealed record CachedBlob(string Sha256, long Length, FileStream Stream) : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// Phase 123. Blobs by the sha256 their upstream named (123 D1), written to <c>partial/</c>, hashed as they are
/// written and moved to <c>blobs/</c> only when the hash matches (D2). One fill per blob at a time (D3).
/// </summary>
public sealed class RelayCache : IDisposable
{
    private const double GiB = 1024d * 1024 * 1024;

    private readonly IOptionsMonitor<RelayOptions> options;
    private readonly ILogger<RelayCache> logger;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> filling = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private string? root;
    private long hits, misses, filled, discarded, skipped, served;

    public RelayCache(IOptionsMonitor<RelayOptions> options, ILogger<RelayCache> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public bool Enabled => options.CurrentValue is { Enabled: true, Cache.Enabled: true };

    /// <summary>The kept blob, or null — a miss is counted only where the cache was asked (a blob it could hold).</summary>
    public CachedBlob? Open(string sha256)
    {
        if (!Enabled)
        {
            return null;
        }

        var directory = Root();

        lock (gate)
        {
            if (!entries.TryGetValue(sha256, out var entry))
            {
                Interlocked.Increment(ref misses);
                return null;
            }

            try
            {
                var path = BlobPath(directory, sha256);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);
                entry.LastServedUtc = DateTime.UtcNow;
                Touch(path, entry.LastServedUtc);
                Interlocked.Increment(ref hits);
                return new CachedBlob(sha256, stream.Length, stream);
            }
            catch (IOException)
            {
                // Somebody emptied the directory under a running hub: that is a download, never a wrong answer (D4).
                entries.Remove(sha256);
                Interlocked.Increment(ref misses);
                return null;
            }
        }
    }

    public void Served(long bytes) => Interlocked.Add(ref served, bytes);

    /// <summary>
    /// Starts keeping a blob as it passes (D3): null when another fill holds it, its length is unknown, or it does not
    /// fit the budget. The caller writes every byte and calls <see cref="RelayFill.Complete"/>, or disposes it.
    /// </summary>
    public RelayFill? Begin(string sha256, long? length)
        => Enabled && filling.TryAdd(sha256, 0) ? Reserve(sha256, length) : null;

    /// <summary>
    /// A ranged miss (D3): the node's range is relayed as before, and the hub fetches the whole blob once, beside it.
    /// <paramref name="fetch"/> answers the whole GET, redirects followed.
    /// </summary>
    public void FillInBackground(string sha256, Func<CancellationToken, Task<HttpResponseMessage>> fetch)
    {
        if (!Enabled || !filling.TryAdd(sha256, 0))
        {
            return;
        }

        var token = stopping.Token;

        _ = Task.Run(async () =>
        {
            RelayFill? fill = null;

            try
            {
                using var response = await fetch(token);

                if ((int)response.StatusCode != StatusCodes.Status200OK)
                {
                    logger.LogInformation("Relay cache: not keeping {Digest}, the upstream answered {Status}", Short(sha256), (int)response.StatusCode);
                    filling.TryRemove(sha256, out _);
                    return;
                }

                fill = Reserve(sha256, response.Content.Headers.ContentLength);

                if (fill is null)
                {
                    return;
                }

                await using var body = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[81920];
                int read;

                while ((read = await body.ReadAsync(buffer, token)) > 0)
                {
                    fill.Write(buffer.AsSpan(0, read));
                }

                fill.Complete();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                logger.LogWarning("Relay cache: fetching {Digest} for keeping failed: {Reason}", Short(sha256), ex.Message);
                filling.TryRemove(sha256, out _);
            }
            finally
            {
                fill?.Dispose();
            }
        }, CancellationToken.None);
    }

    public RelayCacheSnapshot? Snapshot()
    {
        if (!Enabled)
        {
            return null;
        }

        Root();

        lock (gate)
        {
            return new RelayCacheSnapshot(
                entries.Count,
                entries.Values.Sum(entry => entry.Length),
                MaxBytes(),
                Interlocked.Read(ref hits),
                Interlocked.Read(ref misses),
                Interlocked.Read(ref filled),
                Interlocked.Read(ref discarded),
                Interlocked.Read(ref skipped),
                Interlocked.Read(ref served));
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }

    /// <summary>A blob's digest as the upstream names it: 64 lowercase hex characters, or nothing.</summary>
    public static string? Sha256Of(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (text.StartsWith("W/", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        text = text.Trim('"');

        if (text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["sha256:".Length..];
        }

        return text.Length == 64 && text.All(Uri.IsHexDigit) ? text.ToLowerInvariant() : null;
    }

    private RelayFill? Reserve(string sha256, long? length)
    {
        var cache = options.CurrentValue.Cache;
        var directory = Root();

        if (length is not > 0)
        {
            return Skip(sha256, "its length is unknown");
        }

        var size = length.Value;

        if (size > MaxBytes())
        {
            return Skip(sha256, $"{size / GiB:0.##} GiB is more than Relay:Cache:MaxGiB");
        }

        lock (gate)
        {
            if (entries.ContainsKey(sha256))
            {
                filling.TryRemove(sha256, out _);
                return null;
            }

            var reserved = filling.Values.Sum();
            var used = entries.Values.Sum(entry => entry.Length);

            // The least recently served goes first (D4); a blob being read keeps its file open, which on Linux
            // keeps its bytes until the reader finishes, and on Windows FileShare.Delete allows the delete.
            foreach (var victim in entries.Values.OrderBy(entry => entry.LastServedUtc).ToList())
            {
                if (used + reserved + size <= MaxBytes())
                {
                    break;
                }

                try
                {
                    File.Delete(BlobPath(directory, victim.Sha256));
                    entries.Remove(victim.Sha256);
                    used -= victim.Length;
                    logger.LogInformation("Relay cache: evicted {Digest} ({Bytes} bytes), least recently served", Short(victim.Sha256), victim.Length);
                }
                catch (IOException)
                {
                }
            }

            if (used + reserved + size > MaxBytes())
            {
                return Skip(sha256, "the cache is full of blobs it could not evict");
            }

            if (FreeBytes(directory) is { } free && free - size < cache.KeepFreeGiB * GiB)
            {
                return Skip(sha256, $"it would leave less than Relay:Cache:KeepFreeGiB ({cache.KeepFreeGiB}) free");
            }

            filling[sha256] = size;
        }

        var partial = Path.Combine(directory, "partial", $"{sha256}.{Guid.NewGuid():N}");
        var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920);
        return new RelayFill(this, sha256, size, partial, stream);
    }

    private RelayFill? Skip(string sha256, string reason)
    {
        filling.TryRemove(sha256, out _);
        Interlocked.Increment(ref skipped);
        logger.LogInformation("Relay cache: not keeping {Digest}: {Reason}", Short(sha256), reason);
        return null;
    }

    internal void Finish(RelayFill fill, bool whole, string? actual)
    {
        var directory = Root();

        try
        {
            if (whole && actual == fill.Sha256)
            {
                var path = BlobPath(directory, fill.Sha256);
                File.Move(fill.PartialPath, path, overwrite: true);
                var now = DateTime.UtcNow;

                lock (gate)
                {
                    entries[fill.Sha256] = new Entry(fill.Sha256, fill.Length) { LastServedUtc = now };
                }

                Interlocked.Increment(ref filled);
                logger.LogInformation("Relay cache: kept {Digest} ({Bytes} bytes)", Short(fill.Sha256), fill.Length);
                return;
            }

            File.Delete(fill.PartialPath);

            if (whole)
            {
                // D2: the bytes are not what the upstream said they are. Not kept, and said out loud.
                Interlocked.Increment(ref discarded);
                logger.LogWarning(
                    "Relay cache: discarded {Digest} — the bytes hash to {Actual}; a CDN or a mirror served something else",
                    Short(fill.Sha256), actual is null ? "nothing" : Short(actual));
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning("Relay cache: could not keep {Digest}: {Reason}", Short(fill.Sha256), ex.Message);
        }
        finally
        {
            filling.TryRemove(fill.Sha256, out _);
        }
    }

    /// <summary>Resolved and indexed once, on first use: an enabled cache that is never asked creates nothing.</summary>
    private string Root()
    {
        if (root is { } known)
        {
            return known;
        }

        lock (gate)
        {
            if (root is { } raced)
            {
                return raced;
            }

            var directory = Path.GetFullPath(options.CurrentValue.Cache.DataDirectory);
            var blobs = Directory.CreateDirectory(Path.Combine(directory, "blobs"));
            var partial = Path.Combine(directory, "partial");

            // A fill the last hub was writing when it stopped is not a blob; nothing could finish it.
            if (Directory.Exists(partial))
            {
                Directory.Delete(partial, recursive: true);
            }

            Directory.CreateDirectory(partial);

            foreach (var file in blobs.EnumerateFiles())
            {
                if (Sha256Of(file.Name) is { } sha256 && sha256 == file.Name)
                {
                    entries[sha256] = new Entry(sha256, file.Length) { LastServedUtc = file.LastWriteTimeUtc };
                }
            }

            if (entries.Count > 0)
            {
                logger.LogInformation("Relay cache: {Count} blobs, {Bytes} bytes in {Directory}", entries.Count, entries.Values.Sum(e => e.Length), directory);
            }

            root = directory;
            return directory;
        }
    }

    private long MaxBytes() => (long)(options.CurrentValue.Cache.MaxGiB * GiB);

    private static string BlobPath(string directory, string sha256) => Path.Combine(directory, "blobs", sha256);

    private static long? FreeBytes(string directory)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The order survives a restart: the file's write time is when it was last served.</summary>
    private static void Touch(string path, DateTime when)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, when);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Short(string sha256) => sha256.Length > 12 ? sha256[..12] : sha256;

    private sealed class Entry(string sha256, long length)
    {
        public string Sha256 { get; } = sha256;

        public long Length { get; } = length;

        public DateTime LastServedUtc { get; set; }
    }
}

/// <summary>One blob being kept: every byte written is hashed, and only a whole, matching blob is moved in (123 D2).</summary>
public sealed class RelayFill : IDisposable
{
    private readonly RelayCache cache;
    private readonly FileStream stream;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long written;
    private bool done;

    internal RelayFill(RelayCache cache, string sha256, long length, string partialPath, FileStream stream)
    {
        this.cache = cache;
        this.stream = stream;
        Sha256 = sha256;
        Length = length;
        PartialPath = partialPath;
    }

    public string Sha256 { get; }

    public long Length { get; }

    internal string PartialPath { get; }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        stream.Write(bytes);
        hash.AppendData(bytes);
        written += bytes.Length;
    }

    public void Complete()
    {
        if (done)
        {
            return;
        }

        done = true;
        stream.Dispose();
        var whole = written == Length;
        cache.Finish(this, whole, whole ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null);
        hash.Dispose();
    }

    /// <summary>Not completed: the node went away mid-blob, or the upstream did. Nothing is kept.</summary>
    public void Dispose()
    {
        if (done)
        {
            return;
        }

        done = true;
        stream.Dispose();
        hash.Dispose();
        cache.Finish(this, whole: false, actual: null);
    }
}
