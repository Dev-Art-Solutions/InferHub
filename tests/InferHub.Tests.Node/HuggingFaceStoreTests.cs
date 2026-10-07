using System.Text.Json;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 98: the hub hands a node a Hugging Face link and the node fetches it once — a GGUF into the
/// llama.cpp router's directory (resumed, verified, the router restarted), a checkpoint converted for
/// colibri. The Hub is a real socket (<see cref="FakeHuggingFace"/>); the real one is in the notes.
/// </summary>
public class HuggingFaceStoreTests : IAsyncLifetime
{
    private const string Gguf = "bartowski/Tiny-GGUF";

    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-hf-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Engine, bool Around)> restarts = [];
    private FakeHuggingFace hub = null!;

    private static readonly byte[] Q4 = FakeHuggingFace.Bytes(300_000, 1);
    private static readonly byte[] Q8 = FakeHuggingFace.Bytes(500_000, 2);
    private static readonly byte[] Projector = FakeHuggingFace.Bytes(50_000, 3);

    public async Task InitializeAsync()
    {
        hub = await FakeHuggingFace.StartAsync();
        hub.Repo(Gguf,
            ("README.md", "# tiny"u8.ToArray()),
            ("Tiny-Q4_K_M.gguf", Q4),
            ("Tiny-Q8_0.gguf", Q8));
        hub.Repo("someone/Vision-GGUF",
            ("Vision-Q4_K_M.gguf", Q4),
            ("mmproj-Vision-f16.gguf", Projector));
        hub.Repo("allenai/OLMoE-1B-7B-0924",
            ("config.json", "{}"u8.ToArray()),
            ("model-00001-of-00002.safetensors", Q4),
            ("model-00002-of-00002.safetensors", Q8));
        hub.Repo("someone/Only-Readme", ("README.md", "hi"u8.ToArray()));
    }

    public async Task DisposeAsync()
    {
        await hub.DisposeAsync();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---- D2: GGUF ----------------------------------------------------------------------------

    [Fact]
    public async Task AQuantIsDownloadedVerifiedIntoItsOwnDirectoryAndTheRouterIsRestarted()
    {
        var store = Store();

        var frames = await PullAsync(store, $"https://huggingface.co/{Gguf}:Q4_K_M");

        var model = Path.Combine(Gguf_Dir, "Tiny-Q4_K_M");
        Assert.Equal(Q4, await File.ReadAllBytesAsync(Path.Combine(model, "Tiny-Q4_K_M.gguf")));
        Assert.False(File.Exists(Path.Combine(model, "Tiny-Q8_0.gguf")));
        Assert.Empty(Directory.GetFiles(model, "*.part*"));
        Assert.Equal(["Tiny-Q4_K_M.gguf"], hub.Downloads);

        var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(model, HuggingFaceStore.Marker))).RootElement;
        Assert.Equal(Gguf, marker.GetProperty("Repo").GetString());
        Assert.Equal(FakeHuggingFace.Commit, marker.GetProperty("Commit").GetString());

        Assert.Equal([("gguf", false)], restarts);
        Assert.Contains(frames, f => f.Total == Q4.Length && f.Completed == Q4.Length);
        Assert.Equal("ready as 'Tiny-Q4_K_M' (llama.cpp)", frames[^1].Status);

        // Downloaded once: the second pull fetches nothing and restarts nothing.
        var again = await PullAsync(store, $"hf.co/{Gguf}:q4_k_m");
        Assert.Single(hub.Downloads);
        Assert.Single(restarts);
        Assert.StartsWith("already downloaded as 'Tiny-Q4_K_M'", again[^1].Status);
    }

    [Fact]
    public async Task SeveralModelsAndNoQuantIsRefusedNamingTheQuants()
    {
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => PullAsync(Store(), $"https://huggingface.co/{Gguf}"));

        Assert.Contains("has 2 GGUF models; choose a quantization (Tiny-Q4_K_M, Tiny-Q8_0)", refused.Message);
        Assert.Empty(hub.Downloads);
    }

    [Fact]
    public async Task AFileLinkTakesThatFileAndAProjectorComesAlong()
    {
        await PullAsync(Store(), $"https://huggingface.co/{Gguf}/blob/main/Tiny-Q8_0.gguf");
        Assert.True(File.Exists(Path.Combine(Gguf_Dir, "Tiny-Q8_0", "Tiny-Q8_0.gguf")));

        await PullAsync(Store(), "someone/Vision-GGUF");
        Assert.True(File.Exists(Path.Combine(Gguf_Dir, "Vision-Q4_K_M", "mmproj-Vision-f16.gguf")));
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesWhereItStopped()
    {
        var model = Path.Combine(Gguf_Dir, "Tiny-Q4_K_M");
        Directory.CreateDirectory(model);
        await File.WriteAllBytesAsync(Path.Combine(model, "Tiny-Q4_K_M.gguf.part.partial"), Q4[..120_000]);

        await PullAsync(Store(), $"{Gguf}:Q4_K_M");

        Assert.Equal(["bytes=120000-"], hub.Ranges);
        Assert.Equal(Q4, await File.ReadAllBytesAsync(Path.Combine(model, "Tiny-Q4_K_M.gguf")));
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsSha256IsDeletedAndTheModelIsNotListed()
    {
        hub.Corrupt.Add("Tiny-Q4_K_M.gguf");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => PullAsync(Store(), $"{Gguf}:Q4_K_M"));

        Assert.Contains("does not match its sha256", failed.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(Gguf_Dir, "Tiny-Q4_K_M"), "*.gguf"));
        Assert.Empty(restarts);
    }

    [Fact]
    public async Task AGatedRepoSaysWhatToDoAndATokenIsSent()
    {
        hub.Gated.Add(Gguf);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => PullAsync(Store(), $"{Gguf}:Q4_K_M"));
        Assert.Contains("gated or private", refused.Message);
        Assert.Contains("HuggingFace:Token", refused.Message);

        await PullAsync(Store(token: "hf_secret"), $"{Gguf}:Q4_K_M");
        Assert.Equal("Bearer hf_secret", hub.LastAuthorization);
    }

    [Fact]
    public async Task AGgufWithNoRouterAndARepoWithNothingServableAreRefused()
    {
        var noRouter = Store(gguf: false);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => PullAsync(noRouter, $"{Gguf}:Q4_K_M"));
        Assert.Contains("no llama.cpp router", refused.Message);

        var nothing = await Assert.ThrowsAsync<ArgumentException>(() => PullAsync(Store(), "someone/Only-Readme"));
        Assert.Contains("nothing here llama.cpp or colibri can serve", nothing.Message);
    }

    // ---- D3: a checkpoint for colibri ----------------------------------------------------------

    [Fact]
    public async Task ACheckpointIsConvertedIntoTheCatalogueOnce()
    {
        var converter = new FakeConverter();
        var store = Store(converter: converter);

        var frames = await PullAsync(store, "https://huggingface.co/allenai/OLMoE-1B-7B-0924");

        // It converts into a directory the catalogue cannot list (found live: config.json comes early)…
        var (repo, staging) = Assert.Single(converter.Calls);
        Assert.Equal("allenai/OLMoE-1B-7B-0924", repo);
        Assert.Equal(Path.Combine(Colibri_Dir, ".converting-olmoe-1b-7b-0924"), staging);
        Assert.False(ColibriOptions.IsModelName(Path.GetFileName(staging)));

        // …and the model appears under its name only when whole.
        var directory = Path.Combine(Colibri_Dir, "olmoe-1b-7b-0924");
        Assert.False(Directory.Exists(staging));
        Assert.True(File.Exists(Path.Combine(directory, "config.json")));
        Assert.True(File.Exists(Path.Combine(directory, HuggingFaceStore.Marker)));
        Assert.Contains(frames, f => f.Status == "coli convert: checkpoint: OLMoE -> tools/convert_olmoe_merged.py");
        Assert.Equal("ready as 'olmoe-1b-7b-0924' (colibri)", frames[^1].Status);
        Assert.Empty(hub.Downloads);   // colibri downloads the shards itself

        await PullAsync(store, "allenai/OLMoE-1B-7B-0924");
        Assert.Single(converter.Calls);
    }

    [Fact]
    public async Task AFailedConversionLeavesNothingTheCatalogueWouldList()
    {
        var converter = new FakeConverter { Fail = true };

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => PullAsync(Store(converter: converter), "allenai/OLMoE-1B-7B-0924"));

        Assert.Contains("unsupported checkpoint family", failed.Message);
        Assert.False(Directory.Exists(Path.Combine(Colibri_Dir, "olmoe-1b-7b-0924")));
        Assert.False(Directory.Exists(Path.Combine(Colibri_Dir, ".converting-olmoe-1b-7b-0924")));
    }

    [Fact]
    public async Task ACheckpointWithoutAColibriCatalogueSaysLlamaCppNeedsAGguf()
    {
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => PullAsync(Store(colibriDir: false), "allenai/OLMoE-1B-7B-0924"));

        Assert.Contains("llama.cpp needs a GGUF", refused.Message);
    }

    // ---- D4: delete, and the executor --------------------------------------------------------

    [Fact]
    public async Task DeleteRemovesOnlyWhatTheStoreDownloadedWithTheRouterStoppedAroundIt()
    {
        var store = Store();
        await PullAsync(store, $"{Gguf}:Q4_K_M");
        restarts.Clear();

        var handPlaced = Path.Combine(Gguf_Dir, "mine");
        Directory.CreateDirectory(handPlaced);
        await File.WriteAllTextAsync(Path.Combine(handPlaced, "mine.gguf"), "x");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync("mine", CancellationToken.None));
        Assert.Contains("operator's to remove", refused.Message);
        Assert.True(Directory.Exists(handPlaced));

        await store.DeleteAsync("Tiny-Q4_K_M", CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(Gguf_Dir, "Tiny-Q4_K_M")));
        Assert.Equal([("gguf", true)], restarts);
    }

    [Fact]
    public async Task TheExecutorSendsAHuggingFaceCommandToTheStoreAndEndsWithOneTerminalFrame()
    {
        var backend = new NoManagement();
        var withStore = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance, huggingFace: Store());
        var without = new ModelCommandExecutor(backend, NullLogger<ModelCommandExecutor>.Instance);

        Assert.True(withStore.ManagesModels);
        Assert.False(without.ManagesModels);

        var pulled = await Frames(withStore, new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, $"{Gguf}:Q8_0", Engine: ModelCommand.EngineHuggingFace));
        Assert.Equal("success", pulled[^1].Status);
        Assert.Single(pulled, f => f.Done);

        var bad = await Frames(withStore, new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, Gguf, Engine: ModelCommand.EngineHuggingFace));
        Assert.Contains("choose a quantization", bad[^1].Error);

        var refused = await Frames(without, new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, Gguf, Engine: ModelCommand.EngineHuggingFace));
        Assert.Contains("HuggingFace:Enabled=true", refused[^1].Error);
    }

    [Fact]
    public void ConvertRunsUnderColibrisOwnVenvWithTheTokenAndNeverTheContentTee()
    {
        // Found live: under the image's bare python3, coli convert could not read the checkpoint's
        // family (no huggingface_hub), picked GLM-5.2's converter and refused an OLMoE.
        var launcherDir = Path.Combine(root, "colibri-pkg");
        var venv = Path.Combine(launcherDir, "mio_env", OperatingSystem.IsWindows() ? "Scripts" : "bin");
        Directory.CreateDirectory(venv);
        var python = Path.Combine(venv, OperatingSystem.IsWindows() ? "python.exe" : "python3");
        File.WriteAllText(python, "");

        var colibri = new ColibriOptions { Serve = new ColibriServeOptions { Python = "python3", Launcher = Path.Combine(launcherDir, "coli") } };
        var info = ColibriProcessConverter.StartInfo(
            colibri,
            new HuggingFaceOptions { Token = "hf_x", Endpoint = "https://mirror.example" },
            "/models/.hf-cache",
            "allenai/OLMoE-1B-7B-0924-Instruct",
            "/models/olmoe");

        Assert.Equal(python, info.FileName);
        Assert.Equal([Path.Combine(launcherDir, "coli"), "convert", "--repo", "allenai/OLMoE-1B-7B-0924-Instruct", "--model", "/models/olmoe"], info.ArgumentList);
        Assert.Equal("hf_x", info.Environment["HF_TOKEN"]);
        Assert.Equal("https://mirror.example", info.Environment["HF_ENDPOINT"]);
        Assert.False(info.Environment.ContainsKey(ColibriServe.ContentTeeVariable));

        Assert.Equal("python3", ColibriProcessConverter.Interpreter(new ColibriOptions { Serve = new ColibriServeOptions { Python = "python3", Launcher = "coli" } }));
    }

    // ---- configuration -----------------------------------------------------------------------

    [Fact]
    public void TheValidatorNamesWhatIsMissing()
    {
        static string? Fail(BackendOptions backend, ColibriOptions colibri, HuggingFaceOptions options)
            => new HuggingFaceOptionsValidator(Options.Create(backend), Options.Create(colibri)).Validate(null, options).FailureMessage;

        var on = new HuggingFaceOptions { Enabled = true };
        var router = new EngineOptions { Type = "llamacpp", Serve = new EngineServeOptions { ModelsDir = "/models/gguf" } };

        Assert.Contains("nowhere to put a model", Fail(new BackendOptions(), new ColibriOptions(), on));
        Assert.Null(Fail(new BackendOptions { Engines = { ["gguf"] = router } }, new ColibriOptions(), on));
        Assert.Contains("name the one that receives GGUF downloads",
            Fail(new BackendOptions { Engines = { ["a"] = router, ["b"] = router } }, new ColibriOptions(), on));
        Assert.Contains("that name is reserved",
            Fail(new BackendOptions { Engines = { ["huggingface"] = router } }, new ColibriOptions(), new HuggingFaceOptions()));
        Assert.Null(Fail(
            new BackendOptions { Type = "colibri" },
            new ColibriOptions { Serve = new ColibriServeOptions { ModelsDir = "/models" } },
            on));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private string Gguf_Dir => Path.Combine(root, "gguf");

    private string Colibri_Dir => Path.Combine(root, "colibri");

    private HuggingFaceStore Store(bool gguf = true, bool colibriDir = true, IColibriConverter? converter = null, string? token = null)
    {
        var options = new HuggingFaceOptions { Enabled = true, Endpoint = hub.Url, Token = token };
        var targets = new HuggingFaceTargets(gguf ? "gguf" : null, gguf ? Gguf_Dir : null, colibriDir ? Colibri_Dir : null);

        return new HuggingFaceStore(
            options,
            targets,
            () => hub.Client(token),
            colibriDir ? converter ?? new FakeConverter() : null,
            async (engine, around, ct) =>
            {
                restarts.Add((engine, around is not null));

                if (around is not null)
                {
                    await around();
                }
            },
            colibri: null,
            NullLogger.Instance);
    }

    private static async Task<List<ModelPullProgress>> PullAsync(HuggingFaceStore store, string link)
    {
        var frames = new List<ModelPullProgress>();

        await foreach (var frame in store.PullAsync(link, CancellationToken.None))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static async Task<List<ModelCommandProgress>> Frames(ModelCommandExecutor executor, ModelCommand command)
    {
        var frames = new List<ModelCommandProgress>();

        await foreach (var frame in executor.ExecuteAsync(command, "n", CancellationToken.None))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private sealed class NoManagement : IInferenceBackend
    {
        public string Name => "openai";

        public string Endpoint => "http://x";

        public IReadOnlyList<string> Kinds => [CapabilityKinds.Chat];

        public bool SupportsModelManagement => false;

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelInfo>?>([]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
