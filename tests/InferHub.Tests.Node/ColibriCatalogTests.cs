using InferHub.Node.Backends.Catalog;
using System.Text.Json;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Profiles;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 97: a colibri catalogue — every converted model listed, the one a request names loaded, at
/// most <c>MaxLoaded</c> at once, idle ones stopped on demand, and the hub's pins kept. Each loaded
/// model is a real socket (<see cref="FakeColibriLauncher"/>); the real engine is in the notes.
/// </summary>
public class ColibriCatalogTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    // ---- D1: the catalogue ---------------------------------------------------------------

    [Fact]
    public async Task EveryConvertedModelIsListedBeforeAnyIsLoaded()
    {
        var root = Root("olmoe", "qwen-moe");
        Directory.CreateDirectory(Path.Combine(root, "not-converted"));       // no config.json
        Directory.CreateDirectory(Path.Combine(root, "bad name"));
        File.WriteAllText(Path.Combine(root, "bad name", "config.json"), "{}");

        var extra = Root("glm");
        var launcher = new FakeColibriLauncher();
        var catalog = FakeColibri.Catalog(Options(root, o => o.Serve.Models["glm-5"] = Path.Combine(extra, "glm")), launcher);
        catalog.Start();

        var models = await catalog.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["glm-5", "olmoe", "qwen-moe"], models!.Select(m => m.Name));
        Assert.Empty(launcher.Events);
        Assert.Equal(["chat", "score"], catalog.Kinds);
        Assert.Equal(["chat", "score"], catalog.KindsFor("olmoe"));
        Assert.Null(catalog.KindsFor("llama3"));
        Assert.All(catalog.State("n").Models, m => Assert.Equal(NodeCatalogModel.Unloaded, m.State));
    }

    [Fact]
    public async Task AModelConvertedWhileTheNodeRunsIsListedOnTheNextAsk()
    {
        var root = Root("olmoe");
        var catalog = FakeColibri.Catalog(Options(root), new FakeColibriLauncher());

        Directory.CreateDirectory(Path.Combine(root, "qwen-moe"));
        File.WriteAllText(Path.Combine(root, "qwen-moe", "config.json"), "{}");

        Assert.Equal(["olmoe", "qwen-moe"], (await catalog.ListModelsAsync(CancellationToken.None))!.Select(m => m.Name));
    }

    // ---- D2: admission -------------------------------------------------------------------

    [Fact]
    public async Task ARequestLoadsItsModelOnceAndTheNextOneFindsItLoaded()
    {
        var launcher = new FakeColibriLauncher { LoadDelay = TimeSpan.FromMilliseconds(300) };
        var catalog = Started(Options(Root("olmoe", "qwen-moe")), launcher);

        var first = await catalog.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None);
        var second = await catalog.ChatAsync(FakeColibri.Chat("OLMOE:latest"), CancellationToken.None);

        Assert.Contains("hello from olmoe", first);
        Assert.Contains("hello from olmoe", second);
        Assert.Equal(["launch:olmoe"], launcher.Events);
        Assert.Equal(2, launcher.Chats("olmoe"));
        Assert.EndsWith(Path.Combine("olmoe"), launcher.Directories["olmoe"]);

        var state = catalog.State("n").Models.Single(m => m.Name == "olmoe");
        Assert.Equal(NodeCatalogModel.Loaded, state.State);
        Assert.Equal(0, state.InFlight);
        Assert.NotNull(state.IdleSeconds);
    }

    [Fact]
    public async Task OnOneSlotTheOtherModelIsStoppedBeforeTheNextIsLaunched()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("olmoe", "qwen-moe")), launcher);

        await catalog.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None);
        var reply = await catalog.ChatAsync(FakeColibri.Chat("qwen-moe"), CancellationToken.None);

        // The order is the RAM: the first model's process is gone before the second maps its weights.
        Assert.Contains("hello from qwen-moe", reply);
        Assert.Equal(["launch:olmoe", "stop:olmoe", "launch:qwen-moe"], launcher.Events);
        Assert.Equal(["qwen-moe"], launcher.Alive.Keys);
    }

    [Fact]
    public async Task TwoSlotsKeepTwoModelsAndEvictTheLeastRecentlyUsed()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("a", "b", "c"), o => o.Serve.MaxLoaded = 2), launcher);

        await catalog.ChatAsync(FakeColibri.Chat("a"), CancellationToken.None);
        await catalog.ChatAsync(FakeColibri.Chat("b"), CancellationToken.None);
        await catalog.ChatAsync(FakeColibri.Chat("a"), CancellationToken.None);   // b is now the oldest
        await catalog.ChatAsync(FakeColibri.Chat("c"), CancellationToken.None);

        Assert.Equal(["launch:a", "launch:b", "stop:b", "launch:c"], launcher.Events);
        Assert.Equal(["a", "c"], launcher.Alive.Keys.Order());
    }

    [Fact]
    public async Task AModelOutsideTheCatalogueIsNotFoundAndNamesWhatIsThere()
    {
        var catalog = Started(Options(Root("olmoe")), new FakeColibriLauncher());

        var refused = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.ChatAsync(FakeColibri.Chat("llama3"), CancellationToken.None));

        Assert.True(refused.NotInCatalogue);
        Assert.Contains("no model 'llama3'; it has olmoe", refused.Message);

        var score = await catalog.ScoreAsync(FakeColibri.Score("llama3"), CancellationToken.None);
        Assert.Equal(BrioErrorCodes.ModelNotFound, score.ErrorCode);
    }

    [Fact]
    public async Task AScoreLoadsItsModelAndReachesThatModelsBrio()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("olmoe", "qwen-moe")), launcher);

        var result = await catalog.ScoreAsync(FakeColibri.Score("qwen-moe"), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(["launch:qwen-moe"], launcher.Events);
    }

    [Fact]
    public async Task AProcessThatExitsBeforeItAnswersIsAFailedLoadWithTheExitInTheSentence()
    {
        var launcher = new FakeColibriLauncher();
        launcher.Crashing.Add("olmoe");
        var catalog = Started(Options(Root("olmoe")), launcher);

        var refused = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None));

        Assert.Contains("could not load 'olmoe'", refused.Message);
        Assert.Contains("exited with code 1", refused.Message);
        var state = catalog.State("n").Models.Single();
        Assert.Equal(NodeCatalogModel.Failed, state.State);
        Assert.Contains("exited with code 1", state.LastError);

        // The next request tries again — the operator may have fixed the box.
        launcher.Crashing.Clear();
        Assert.Contains("hello from olmoe", await catalog.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None));
        Assert.Equal(NodeCatalogModel.Loaded, catalog.State("n").Models.Single().State);
    }

    [Fact]
    public async Task AStoppedCatalogueRefusesAndHoldsNothing()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("olmoe")), launcher);
        await catalog.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None);

        await catalog.StopAsync(CancellationToken.None);

        Assert.Empty(launcher.Alive);
        Assert.False(catalog.State("n").Running);
        var refused = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None));
        Assert.Contains("stopped", refused.Message);
    }

    // ---- D3: on demand -------------------------------------------------------------------

    [Fact]
    public async Task OnDemandStopsAnIdleModelAndKeepsAPinnedOne()
    {
        var time = new ShiftableTime();
        var launcher = new FakeColibriLauncher();
        var catalog = Started(
            Options(Root("a", "b"), o =>
            {
                o.Serve.MaxLoaded = 2;
                o.Serve.OnDemand = true;
                o.Serve.IdleUnload = TimeSpan.FromMinutes(10);
                o.Serve.Preload = ["a"];
            }),
            launcher,
            time);

        await WaitAsync(() => catalog.State("n").Models.Single(m => m.Name == "a").State == NodeCatalogModel.Loaded);
        await catalog.ChatAsync(FakeColibri.Chat("b"), CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(9));
        await catalog.SweepAsync(CancellationToken.None);
        Assert.Equal(["a", "b"], launcher.Alive.Keys.Order());

        time.Advance(TimeSpan.FromMinutes(2));
        await catalog.SweepAsync(CancellationToken.None);

        Assert.Equal(["a"], launcher.Alive.Keys);
        Assert.Equal(NodeCatalogModel.Unloaded, catalog.State("n").Models.Single(m => m.Name == "b").State);
        Assert.True(catalog.State("n").Models.Single(m => m.Name == "a").Pinned);
    }

    [Fact]
    public async Task WithoutOnDemandAnIdleModelStaysLoaded()
    {
        var time = new ShiftableTime();
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("a")), launcher, time);
        await catalog.ChatAsync(FakeColibri.Chat("a"), CancellationToken.None);

        time.Advance(TimeSpan.FromHours(5));
        await catalog.SweepAsync(CancellationToken.None);

        Assert.Equal(["a"], launcher.Alive.Keys);
        Assert.False(catalog.State("n").OnDemand);
    }

    // ---- D4: the hub's selection -----------------------------------------------------------

    [Fact]
    public async Task AProfileLoadsItsPinsUnloadsWhatItDroppedAndSwitchesOnDemand()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("a", "b")), launcher);

        var changes = await catalog.ApplyAsync(new CatalogProfile(["a"], OnDemand: true), CancellationToken.None);
        Assert.Contains("colibri 'a' pinned", changes);
        Assert.Contains("colibri on-demand on", changes);
        await WaitAsync(() => launcher.Alive.ContainsKey("a"));
        Assert.True(catalog.State("n").OnDemand);

        // Switching the pin on a one-slot box stops the old model before the new one starts.
        await catalog.ApplyAsync(new CatalogProfile(["b"], OnDemand: true), CancellationToken.None);
        await WaitAsync(() => launcher.Alive.ContainsKey("b") && !launcher.Alive.ContainsKey("a"));
        Assert.Equal(["launch:a", "stop:a", "launch:b"], launcher.Events);

        // A pinned model fills the only slot, so another model is refused naming it rather than evicting it.
        var refused = await Assert.ThrowsAsync<CatalogException>(
            () => catalog.ChatAsync(FakeColibri.Chat("a"), CancellationToken.None));
        Assert.Contains("pinned (b)", refused.Message);

        // No colibri block: back to the box's own Preload (none) and OnDemand (off), and the pin's model goes.
        await catalog.ApplyAsync(null, CancellationToken.None);
        await WaitAsync(() => launcher.Alive.IsEmpty);
        Assert.False(catalog.State("n").OnDemand);
        Assert.True(catalog.FirstApplied.IsCompleted);
    }

    [Fact]
    public async Task AnUnloadCommandStopsAModelAndRefusesAPinnedOne()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = Started(Options(Root("a", "b"), o => o.Serve.MaxLoaded = 2), launcher);
        await catalog.ApplyAsync(new CatalogProfile(["a"]), CancellationToken.None);
        await catalog.WarmAsync("b", CancellationToken.None);
        await WaitAsync(() => launcher.Alive.Count == 2);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UnloadAsync("a", CancellationToken.None));
        Assert.Contains("pinned by this node's profile", refused.Message);

        await catalog.UnloadAsync("b", CancellationToken.None);
        Assert.Equal(["a"], launcher.Alive.Keys);
    }

    [Fact]
    public async Task APullIsRefusedInASentenceAndIsNotOffered()
    {
        var catalog = Started(Options(Root("a")), new FakeColibriLauncher());

        Assert.True(catalog.SupportsModelManagement);
        Assert.False(((IInferenceBackend)catalog).SupportsPull);
        var refused = Assert.Throws<NotSupportedException>(() => catalog.PullAsync("a", CancellationToken.None));
        Assert.Contains("coli convert", refused.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public void TheClampPicksFromTheCatalogueAndNeverPastMaxLoaded()
    {
        var local = new LocalCeiling([], false, [], null, true, ColibriCatalogue: ["a", "b", "c"], ColibriMaxLoaded: 1);

        var result = NodeProfileClamp.Apply(local, Profile(new CatalogProfile(["/etc/passwd", "B", "c"], OnDemand: true)));

        Assert.Equal(["b"], result.Effective.Colibri!.Loaded);
        Assert.True(result.Effective.Colibri.OnDemand);
        Assert.Contains(result.Refusals, r => r.Item == "colibri:/etc/passwd" && r.Reason.Contains("it has a, b, c"));
        Assert.Contains(result.Refusals, r => r.Item == "colibri:c" && r.Reason.Contains("MaxLoaded is 1"));

        var none = NodeProfileClamp.Apply(local with { ColibriCatalogue = null }, Profile(new CatalogProfile(["a"])));
        Assert.Null(none.Effective.Colibri);
        Assert.Contains("no colibri catalogue", Assert.Single(none.Refusals).Reason);

        Assert.Null(NodeProfileClamp.Apply(local, Profile(null)).Effective.Colibri);
    }

    // ---- 95 + 97: a catalogue as one of several engines ------------------------------------

    [Fact]
    public async Task AsAnEngineTheCatalogueStartsAndStopsWithItAndAnUnnamedPullStillGoesToOllama()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = FakeColibri.Catalog(Options(Root("olmoe"), o => o.Serve.Preload = ["olmoe"]), launcher);
        var ollama = new PullingBackend();

        var engines = new MultiBackend(
            [
                new Engine("colibri", BackendOptions.Colibri, catalog, autostart: true),
                new Engine("ollama", BackendOptions.Ollama, ollama, autostart: true)
            ],
            TimeSpan.FromSeconds(2),
            TimeProvider.System,
            NullLogger.Instance);

        await engines.StartAsync(CancellationToken.None);
        await WaitAsync(() => launcher.Alive.ContainsKey("olmoe"));
        await engines.ListModelsAsync(CancellationToken.None);

        Assert.True(engines.State("n").Engines.Single(e => e.Name == "colibri").Launched);

        await foreach (var _ in engines.PullAsync("llama3", engine: null, CancellationToken.None))
        {
        }

        Assert.Equal(["llama3"], ollama.Pulled);

        var reply = await engines.ChatAsync(FakeColibri.Chat("olmoe"), CancellationToken.None);
        Assert.Contains("hello from olmoe", reply);

        await engines.ApplyAsync(new Dictionary<string, bool> { ["colibri"] = false }, CancellationToken.None);
        Assert.Empty(launcher.Alive);
    }

    // ---- configuration ---------------------------------------------------------------------

    [Theory]
    [InlineData("Colibri:Serve:Model", "/models/colibri", "are both set")]
    [InlineData("Colibri:Serve:ModelId", "olmoe", "names the single Serve:Model")]
    [InlineData("Colibri:Serve:MaxLoaded", "0", "MaxLoaded must be between 1 and 16")]
    [InlineData("Colibri:Serve:Preload:1", "b", "names 2 models and Colibri:Serve:MaxLoaded is 1")]
    [InlineData("Colibri:Serve:Models:bad name", "/x", "is not a model name")]
    public void AHalfWrittenCatalogueFailsStartupNamingTheKey(string key, string value, string expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backend:Type"] = "colibri",
            ["Colibri:Serve:ModelsDir"] = "/models",
            ["Colibri:Serve:Preload:0"] = "a",
            [key] = value
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = configuration.GetSection("Colibri").Get<ColibriOptions>()!;
        var validator = new ColibriOptionsValidator(
            Microsoft.Extensions.Options.Options.Create(configuration.GetSection("Backend").Get<BackendOptions>()!),
            configuration);

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expected, result.FailureMessage);
    }

    [Fact]
    public void ACatalogueWithItsDefaultsIsValid()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:Type"] = "colibri",
            ["Colibri:Serve:ModelsDir"] = "/models"
        }).Build();

        var validator = new ColibriOptionsValidator(
            Microsoft.Extensions.Options.Options.Create(configuration.GetSection("Backend").Get<BackendOptions>()!),
            configuration);

        Assert.True(validator.Validate(null, configuration.GetSection("Colibri").Get<ColibriOptions>()!).Succeeded);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private string Root(params string[] models)
    {
        var root = FakeColibri.Catalogue(models);
        roots.Add(root);
        return root;
    }

    private static ColibriOptions Options(string root, Action<ColibriOptions>? configure = null)
    {
        var options = new ColibriOptions
        {
            Serve = new ColibriServeOptions { ModelsDir = root, LoadTimeout = TimeSpan.FromSeconds(10) }
        };

        configure?.Invoke(options);
        return options;
    }

    private static ColibriCatalog Started(ColibriOptions options, FakeColibriLauncher launcher, TimeProvider? time = null)
    {
        var catalog = FakeColibri.Catalog(options, launcher, time);
        catalog.Start();
        return catalog;
    }

    private static NodeProfile Profile(CatalogProfile? colibri)
        => new("p", 1, new NodeProfileSelector(NodeId: "n"), Colibri: colibri);

    private static async Task WaitAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 400 && !predicate(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(predicate(), "timed out");
    }

    private sealed class PullingBackend : IInferenceBackend
    {
        public List<string> Pulled { get; } = [];

        public string Name => "ollama";

        public string Endpoint => "http://127.0.0.1:11434";

        public IReadOnlyList<string> Kinds => [CapabilityKinds.Chat, CapabilityKinds.Embed];

        public bool SupportsModelManagement => true;

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>?>([new ModelInfo("llama3:latest", null, null)]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<ModelPullProgress> PullAsync(string model, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Pulled.Add(model);
            await Task.CompletedTask;
            yield return new ModelPullProgress("success", null, null);
        }

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WarmAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
