using System.Security.Cryptography;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 123 D2/D4: a blob is kept only whole and matching, within its budget, least recently served first out.</summary>
public sealed class RelayCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-relay-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"A3DE0000000000000000000000000000000000000000000000000000000000ff\"", "a3de0000000000000000000000000000000000000000000000000000000000ff")]
    [InlineData("sha256:a3de0000000000000000000000000000000000000000000000000000000000ff", "a3de0000000000000000000000000000000000000000000000000000000000ff")]
    [InlineData("W/\"a3de0000000000000000000000000000000000000000000000000000000000ff\"", "a3de0000000000000000000000000000000000000000000000000000000000ff")]
    [InlineData("\"0123456789abcdef0123456789abcdef01234567\"", null)]
    [InlineData("sha256:../../etc/passwd", null)]
    [InlineData(null, null)]
    public void ADigestIsSixtyFourHexCharactersOrNothing(string? value, string? expected)
        => Assert.Equal(expected, RelayCache.Sha256Of(value));

    [Theory]
    [InlineData("v2/library/qwen3/blobs/sha256:a3de0000000000000000000000000000000000000000000000000000000000ff", true)]
    [InlineData("v2/library/qwen3/manifests/8b", false)]
    [InlineData("v2/library/qwen3/blobs/sha256:short", false)]
    public void ARegistryBlobIsAddressedByItsPath(string path, bool kept)
    {
        using var first = new HttpResponseMessage(System.Net.HttpStatusCode.TemporaryRedirect);
        Assert.Equal(kept, NodeRelayEndpoints.DigestOf(NodeRelay.OllamaRegistry, path, first) is not null);
    }

    [Fact]
    public void AHuggingFaceFileIsAddressedOnlyByTheHashItsFirstHopNamed()
    {
        var sha = new string('b', 64);
        using var redirect = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
        redirect.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{sha}\"");
        using var plain = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        plain.Headers.TryAddWithoutValidation("ETag", "\"0123456789abcdef0123456789abcdef01234567\"");

        Assert.Equal(sha, NodeRelayEndpoints.DigestOf(NodeRelay.HuggingFace, "o/r/resolve/main/model.gguf", redirect));
        Assert.Null(NodeRelayEndpoints.DigestOf(NodeRelay.HuggingFace, "o/r/resolve/main/config.json", plain));
        Assert.Null(NodeRelayEndpoints.DigestOf(NodeRelay.HuggingFace, "api/models/o/r", redirect));
    }

    [Fact]
    public void AWholeMatchingBlobIsKeptAndServed()
    {
        var cache = Cache();
        var (bytes, sha) = Blob(5000, 1);

        Assert.Null(cache.Open(sha));
        Keep(cache, sha, bytes);

        using var blob = cache.Open(sha)!;
        Assert.Equal(bytes.Length, blob.Length);
        var snapshot = cache.Snapshot()!;
        Assert.Equal((1, 5000L, 1L, 1L, 1L), (snapshot.Entries, snapshot.Bytes, snapshot.Hits, snapshot.Misses, snapshot.Filled));
    }

    [Fact]
    public void BytesThatDoNotHashToTheDigestAreDiscarded()
    {
        var cache = Cache();
        var (bytes, sha) = Blob(5000, 2);
        bytes[100] ^= 0xFF;

        Keep(cache, sha, bytes);

        Assert.Null(cache.Open(sha));
        Assert.Equal(1, cache.Snapshot()!.Discarded);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "partial")));
    }

    [Fact]
    public void AnUnfinishedFillLeavesNothing()
    {
        var cache = Cache();
        var (bytes, sha) = Blob(5000, 3);

        using (var fill = cache.Begin(sha, bytes.Length)!)
        {
            fill.Write(bytes.AsSpan(0, 1000));
        }

        Assert.Null(cache.Open(sha));
        Assert.Equal(0, cache.Snapshot()!.Discarded);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "partial")));
        // And the blob can be filled again.
        using var again = cache.Begin(sha, bytes.Length);
        Assert.NotNull(again);
    }

    [Fact]
    public void OneFillPerBlobAtATime()
    {
        var cache = Cache();
        var (bytes, sha) = Blob(100, 4);

        using var first = cache.Begin(sha, bytes.Length);
        Assert.NotNull(first);
        Assert.Null(cache.Begin(sha, bytes.Length));
    }

    [Fact]
    public void TheLeastRecentlyServedGoesFirst()
    {
        // Room for two 4000-byte blobs, not three.
        var cache = Cache(maxBytes: 9000);
        var (a, shaA) = Blob(4000, 5);
        var (b, shaB) = Blob(4000, 6);
        var (c, shaC) = Blob(4000, 7);

        Keep(cache, shaA, a);
        Keep(cache, shaB, b);
        Thread.Sleep(20);
        cache.Open(shaA)!.Stream.Dispose();

        Keep(cache, shaC, c);

        Assert.NotNull(cache.Open(shaA));
        Assert.Null(cache.Open(shaB));
        Assert.NotNull(cache.Open(shaC));
    }

    [Fact]
    public void ABlobBiggerThanTheCacheIsNotKept()
    {
        var cache = Cache(maxBytes: 1000);
        var (bytes, sha) = Blob(4000, 8);

        Assert.Null(cache.Begin(sha, bytes.Length));
        Assert.Null(cache.Begin(new string('c', 64), null));
        Assert.Equal(2, cache.Snapshot()!.Skipped);
    }

    [Fact]
    public void ARestartFindsWhatWasKeptAndDropsWhatWasHalfWritten()
    {
        var (bytes, sha) = Blob(3000, 9);
        Keep(Cache(), sha, bytes);
        File.WriteAllBytes(Path.Combine(directory, "partial", sha + ".deadbeef"), new byte[10]);

        var again = Cache();

        using var blob = again.Open(sha)!;
        Assert.Equal(3000, blob.Length);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "partial")));
    }

    [Fact]
    public void OffMeansNothingIsCreated()
    {
        var cache = new RelayCache(new Monitor(new RelayOptions { Enabled = true, Cache = { DataDirectory = directory } }), NullLogger<RelayCache>.Instance);

        Assert.False(cache.Enabled);
        Assert.Null(cache.Open(new string('a', 64)));
        Assert.Null(cache.Snapshot());
        Assert.False(Directory.Exists(directory));
    }

    private RelayCache Cache(long maxBytes = 1L << 30)
        => new(new Monitor(new RelayOptions
        {
            Enabled = true,
            Cache = { Enabled = true, DataDirectory = directory, MaxGiB = maxBytes / (1024d * 1024 * 1024), KeepFreeGiB = 0 }
        }), NullLogger<RelayCache>.Instance);

    private static void Keep(RelayCache cache, string sha, byte[] bytes)
    {
        using var fill = cache.Begin(sha, bytes.Length)!;
        fill.Write(bytes.AsSpan(0, bytes.Length / 2));
        fill.Write(bytes.AsSpan(bytes.Length / 2));
        fill.Complete();
    }

    private static (byte[] Bytes, string Sha) Blob(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return (bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private sealed class Monitor(RelayOptions value) : IOptionsMonitor<RelayOptions>
    {
        public RelayOptions CurrentValue => value;

        public RelayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RelayOptions, string?> listener) => null;
    }
}
