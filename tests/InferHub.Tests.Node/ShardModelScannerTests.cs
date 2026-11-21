using InferHub.Node.Backends.Shards;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 144 D2: a node lists the GGUF files it could head a group with — the files <see cref="ShardGroupHost.ModelFile"/>
/// finds (the directory, then one level down), a split model once by its first part with every part's bytes, and nothing it
/// cannot read as a GGUF.
/// </summary>
public sealed class ShardModelScannerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-shard-models-" + Guid.NewGuid().ToString("N"));

    public ShardModelScannerTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task ItListsTheFilesAHeadWouldFindWithTheirLayersAndKvAndSkipsWhatIsNotAGguf()
    {
        var model = GgufWriter.Llama(layers: 6, heads: 8, kvHeads: 2, keyLength: 64);
        File.WriteAllBytes(Path.Combine(directory, "small.gguf"), model);
        Directory.CreateDirectory(Path.Combine(directory, "pulled"));
        File.WriteAllBytes(Path.Combine(directory, "pulled", "pulled.gguf"), model);
        File.WriteAllBytes(Path.Combine(directory, "pulled", "small.gguf"), model); // the top-level one wins, as ModelFile does
        File.WriteAllBytes(Path.Combine(directory, "broken.gguf"), [1, 2, 3, 4, 5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(directory, "notes.txt"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "big-00001-of-00002.gguf"), model);
        File.WriteAllBytes(Path.Combine(directory, "big-00002-of-00002.gguf"), new byte[3 * 1024 * 1024]);

        var scanner = new ShardModelScanner(directory, NullLogger.Instance);

        var files = await scanner.RefreshAsync();

        Assert.NotNull(files);
        Assert.Equal(["big-00001-of-00002.gguf", "small.gguf", "pulled.gguf"], files.Select(f => f.Name));
        var small = files.Single(f => f.Name == "small.gguf");
        Assert.Equal(6, small.Layers);
        Assert.Equal(6L * 2 * (64 + 64) * 2, small.KvBytesPerToken);
        Assert.Equal(3, files.Single(f => f.Name.StartsWith("big", StringComparison.Ordinal)).MiB);
        Assert.Same(files, scanner.Current());
    }

    [Fact]
    public async Task ANewFileIsSeenOnTheNextScanAndSaidAsAChange()
    {
        var scanner = new ShardModelScanner(directory, NullLogger.Instance, every: TimeSpan.Zero);
        var changes = 0;
        scanner.Changed += () => changes++;

        Assert.Empty((await scanner.RefreshAsync())!);
        File.WriteAllBytes(Path.Combine(directory, "m.gguf"), GgufWriter.Llama());
        Assert.Equal(["m.gguf"], (await scanner.RefreshAsync())!.Select(f => f.Name));
        await scanner.RefreshAsync();

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task WithoutADirectoryItSaysNothingSoTheHubNeverPlacesAHeadThere()
    {
        var scanner = new ShardModelScanner(null, NullLogger.Instance);

        Assert.Null(scanner.Current());
        Assert.Null(await scanner.RefreshAsync());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
