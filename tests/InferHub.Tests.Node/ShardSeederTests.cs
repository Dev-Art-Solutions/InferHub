using InferHub.Node.Backends.Shards;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 143 on the node: a member fills its worker's cache with exactly the tensors of the layers it will hold — each in a file
/// named by the FNV-1a of its bytes, as <c>rpc-server -c</c> looks them up — never shows the worker a half-written one, does
/// not read the file again for an assignment it already filled, and says so when it cannot (no file, no disk, no cache).
/// </summary>
public sealed class ShardSeederTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-seeder-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> data = new()
    {
        ["token_embd.weight"] = Fill(64, 1),
        ["blk.0.attn_q.weight"] = Fill(68, 2),
        ["blk.1.attn_q.weight"] = Fill(68, 3),
        ["blk.2.attn_q.weight"] = Fill(68, 4),
        ["blk.2.attn_norm.weight"] = Fill(8, 5),
        ["blk.3.attn_q.weight"] = Fill(72, 6),
        ["output.weight"] = Fill(80, 7)
    };

    private string Models => Path.Combine(root, "models");
    private string Cache => Path.Combine(root, "cache");
    private string Rpc => Path.Combine(Cache, "rpc");

    public ShardSeederTests()
    {
        Directory.CreateDirectory(Models);
        Write(Path.Combine(Models, "m.gguf"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string path) =>
        File.WriteAllBytes(path, GgufWriter.WithData([("general.architecture", "llama"), ("llama.block_count", 4L)], data.Select(d => (d.Key, d.Value))));

    private ShardSeeder Seeder(bool cache = true, Func<string, long?>? free = null, string? models = null) =>
        new(new ShardOptions { Enabled = true, RamGiB = 4, Cache = cache }, models ?? Models, cache ? Cache : null, NullLogger.Instance, null,
            threshold: 16, freeBytes: free ?? (_ => long.MaxValue));

    /// <summary>Four layers over a head and one member of equal weight: by llama.cpp's rule the member holds 3 and the output.</summary>
    private static ShardSeedList Second(string model = "m.gguf") => new([new ShardSeedSpec("k1", "g", model, [1, 1], 1)]);

    private static async Task<ShardSeedState> Settled(ShardSeeder seeder)
    {
        for (var i = 0; i < 200; i++)
        {
            if (seeder.State() is [var state] && state.State != ShardSeedState.Seeding)
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the assignment did not settle");
    }

    [Fact]
    public async Task AMemberFillsItsCacheWithItsOwnLayersNamedAsTheWorkerLooksThemUp()
    {
        Assert.Equal((3, 1, true), ShardLayers.Split(4, [1, 1])[1]);
        await using var seeder = Seeder();
        await seeder.StartAsync(CancellationToken.None);

        seeder.SetSeeds(Second());
        var state = await Settled(seeder);

        Assert.Equal(ShardSeedState.Ready, state.State);
        var expected = new[] { "blk.3.attn_q.weight", "output.weight" }
            .ToDictionary(n => ShardSeedPlan.FileName(ShardSeedPlan.Fnv1a(data[n])), n => data[n]);
        var files = Directory.GetFiles(Rpc).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(expected.Keys.Order(), files);
        Assert.All(expected, e => Assert.Equal(e.Value, File.ReadAllBytes(Path.Combine(Rpc, e.Key))));
    }

    [Fact]
    public async Task AnAssignmentFilledBeforeIsNotReadAgain()
    {
        await using (var first = Seeder())
        {
            await first.StartAsync(CancellationToken.None);
            first.SetSeeds(Second());
            Assert.Equal(ShardSeedState.Ready, (await Settled(first)).State);
        }

        var written = Directory.GetFiles(Rpc).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        await Task.Delay(50);

        await using var again = Seeder(free: _ => 0); // would refuse if it tried to write anything
        await again.StartAsync(CancellationToken.None);
        again.SetSeeds(Second());

        Assert.Equal(ShardSeedState.Ready, (await Settled(again)).State);
        Assert.All(written, w => Assert.Equal(w.Value, File.GetLastWriteTimeUtc(w.Key)));
    }

    [Fact]
    public async Task AMemberWithoutTheFileSaysSoAndTheHeadSendsTheLayers()
    {
        await using var seeder = Seeder();
        await seeder.StartAsync(CancellationToken.None);

        seeder.SetSeeds(Second("other.gguf"));

        var state = await Settled(seeder);
        Assert.Equal(ShardSeedState.Absent, state.State);
        Assert.Contains("'other.gguf' is not in", state.Error);
        Assert.False(Directory.Exists(Rpc));
    }

    [Fact]
    public async Task AFileTheHubHadDownloadedIsFoundOneDirectoryDown()
    {
        // 98's store: <ModelsDir>/<name>/<file>.
        var store = Path.Combine(root, "store");
        Directory.CreateDirectory(Path.Combine(store, "m"));
        Write(Path.Combine(store, "m", "m.gguf"));
        await using var seeder = Seeder(models: store);
        await seeder.StartAsync(CancellationToken.None);

        seeder.SetSeeds(Second());

        Assert.Equal(ShardSeedState.Ready, (await Settled(seeder)).State);
    }

    [Fact]
    public async Task ADiskWithoutRoomIsSaidAndNothingIsWritten()
    {
        await using var seeder = Seeder(free: _ => 1024);
        await seeder.StartAsync(CancellationToken.None);

        seeder.SetSeeds(Second());

        var state = await Settled(seeder);
        Assert.Equal(ShardSeedState.Failed, state.State);
        Assert.Contains("MiB free (1 GiB kept)", state.Error);
        Assert.Empty(Directory.GetFiles(Rpc));
    }

    [Fact]
    public async Task ADroppedAssignmentLeavesNoPartFileAndNoState()
    {
        await using var seeder = Seeder();
        await seeder.StartAsync(CancellationToken.None);
        seeder.SetSeeds(Second());
        await Settled(seeder);

        seeder.SetSeeds(new ShardSeedList([]));

        Assert.Empty(seeder.State()!);
        Assert.DoesNotContain(Directory.GetFiles(Rpc), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public void AWorkerWithoutItsCacheCannotSeedAndTheHubNeverWaitsForIt()
    {
        var seeder = Seeder(cache: false);

        seeder.SetSeeds(Second());

        Assert.False(seeder.Enabled);
        Assert.Null(seeder.State());
    }

    [Fact]
    public void TheCacheIsWhereTheWorkerWillLookForIt()
    {
        string? Env(string name) => name switch
        {
            "HOME" => "/home/app",
            "LOCALAPPDATA" => @"C:\Users\x\AppData\Local",
            _ => null
        };

        Assert.Null(ShardWorker.CacheRoot(new ShardOptions { Cache = false, CacheDir = "/x" }, Env));
        Assert.Equal("/data/llama", ShardWorker.CacheRoot(new ShardOptions { CacheDir = "/data/llama" }, Env));
        Assert.Equal("/set", ShardWorker.CacheRoot(new ShardOptions(), n => n == "LLAMA_CACHE" ? "/set" : Env(n)));

        var expected = OperatingSystem.IsWindows() ? Path.Combine(@"C:\Users\x\AppData\Local", "llama.cpp")
            : OperatingSystem.IsMacOS() ? Path.Combine("/home/app", "Library", "Caches", "llama.cpp")
            : Path.Combine("/home/app", ".cache", "llama.cpp");
        Assert.Equal(expected, ShardWorker.CacheRoot(new ShardOptions(), Env));
    }

    private static byte[] Fill(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
