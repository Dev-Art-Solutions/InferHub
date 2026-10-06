using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Shared.Brio;
using InferHub.Shared.Contracts;
using InferHub.Shared.LlamaCpp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 96: a llama.cpp engine as the whole of llama.cpp — a router over many GGUFs, its model
/// management, its native routes and its reranker. The router here is a stub of b11417's wire,
/// written from the live run (release notes); the live run itself drove a real one.
/// </summary>
public class LlamaCppRouterTests
{
    // ---- D1: the command line and the INI ---------------------------------------------------

    [Fact]
    public void ARouterIsLaunchedWithoutAModelAndWithItsDirectoryPresetsAndLimit()
    {
        var engine = Router(e =>
        {
            e.Serve.ModelsDir = "/models";
            e.Serve.MaxLoaded = 2;
            e.Serve.Presets["nomic"] = new LlamaCppPresetOptions { Model = "/models/nomic.gguf", Embeddings = true };
            e.Serve.Arguments = ["-c", "4096"];
        });

        var args = LlamaCppServe.StartInfo(engine, "/tmp/p.ini").ArgumentList.ToArray();

        Assert.DoesNotContain("-m", args);
        Assert.DoesNotContain("--embeddings", args);
        Assert.Equal(
            ["--models-dir", "/models", "--models-preset", "/tmp/p.ini", "--models-max", "2", "--host", "127.0.0.1", "--port", "8080", "-c", "4096"],
            args);
    }

    [Fact]
    public void OneModelIsLaunchedAsInV360AndAReRankerSaysSo()
    {
        var engine = new EngineOptions { Type = "llamacpp", Reranking = true, Serve = { Model = "/m/bge.gguf" } };

        var args = LlamaCppServe.StartInfo(engine).ArgumentList.ToArray();

        Assert.Equal(["-m", "/m/bge.gguf", "--alias", "bge", "--host", "127.0.0.1", "--port", "8080", "--reranking"], args);
    }

    [Fact]
    public void ThePresetIniIsOneSortedSectionPerModelWithItsOwnKeys()
    {
        var engine = Router(e =>
        {
            e.Serve.Presets["qwen"] = new LlamaCppPresetOptions { HfRepo = "bartowski/Qwen:Q4_K_M", Settings = { ["ctx-size"] = "8192" } };
            e.Serve.Presets["bge"] = new LlamaCppPresetOptions { Model = "/m/bge.gguf", Reranking = true };
        });

        Assert.Equal(
            "[bge]\nmodel = /m/bge.gguf\nreranking = true\n\n[qwen]\nhf-repo = bartowski/Qwen:Q4_K_M\nctx-size = 8192\n\n",
            LlamaCppServe.PresetIni(engine));
    }

    [Theory]
    [InlineData("Backend:Engines:g:Serve:Model", "/m/x.gguf", "Set one shape")]
    [InlineData("Backend:Engines:g:Embeddings", "true", "Say it per model")]
    [InlineData("Backend:Engines:g:Serve:Presets:p:Settings:verbose", "1", "design rule 7")]
    [InlineData("Backend:Engines:g:Serve:Presets:p:Settings:port", "9", "the node's to set")]
    [InlineData("Backend:Engines:g:Serve:Presets:p:Settings:--ctx-size", "9", "not a llama.cpp long option")]
    [InlineData("Backend:Engines:g:Serve:Presets:p:HfRepo", "not a repo", "is not owner/repo")]
    [InlineData("Backend:Engines:g:Serve:Presets:p:Reranking", "true", "Embeddings and Reranking are both set")]
    [InlineData("Backend:Engines:g:Serve:MaxLoaded", "-1", "MaxLoaded")]
    public void ARouterConfigurationThatCannotBeWrittenIsAStartupFailure(string key, string value, string expected)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backend:Engines:g:Type"] = "llamacpp",
            ["Backend:Engines:g:Serve:ModelsDir"] = "/models",
            ["Backend:Engines:g:Serve:Presets:p:Model"] = "/models/p.gguf",
            ["Backend:Engines:g:Serve:Presets:p:Embeddings"] = "true",
            [key] = value
        };

        var failure = Assert.Throws<OptionsValidationException>(() => Validate(settings));
        Assert.Contains(expected, failure.Message);
    }

    [Fact]
    public void APresetWithNoModelIsRefusedUnlessItAddsSettingsToAFileInTheDirectory()
    {
        var withoutDir = new Dictionary<string, string?>
        {
            ["Backend:Engines:g:Type"] = "llamacpp",
            ["Backend:Engines:g:Serve:Presets:p:Settings:ctx-size"] = "4096"
        };

        Assert.Contains("set Model", Assert.Throws<OptionsValidationException>(() => Validate(withoutDir)).Message);

        withoutDir["Backend:Engines:g:Serve:ModelsDir"] = "/models";
        Validate(withoutDir);
    }

    [Fact]
    public void RouterIsAClaimAboutABaseUrlNotALaunch()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backend:Engines:g:Type"] = "llamacpp",
            ["Backend:Engines:g:Router"] = "true"
        };

        Assert.Contains("Router needs BaseUrl", Assert.Throws<OptionsValidationException>(() => Validate(settings)).Message);

        settings["Backend:Engines:g:BaseUrl"] = "http://127.0.0.1:8080/v1";
        Validate(settings);
    }

    // ---- D2: kinds from configuration ----------------------------------------------------

    [Fact]
    public void APresetSaysWhatItsModelIsForAndEverythingElseChats()
    {
        var (backend, _) = Backend(Router(e =>
        {
            e.Serve.ModelsDir = "/models";
            e.Serve.Presets["nomic"] = new LlamaCppPresetOptions { Model = "/m/n.gguf", Embeddings = true };
            e.Serve.Presets["bge"] = new LlamaCppPresetOptions { Model = "/m/b.gguf", Reranking = true };
        }));

        Assert.Equal([CapabilityKinds.Embed, CapabilityKinds.LlamaCpp], backend.KindsFor("nomic"));
        Assert.Equal([CapabilityKinds.Rerank, CapabilityKinds.LlamaCpp], backend.KindsFor("BGE"));
        Assert.Equal([CapabilityKinds.Chat, CapabilityKinds.LlamaCpp], backend.KindsFor("bartowski/SmolLM2:Q4_K_M"));
        Assert.Equal([CapabilityKinds.Chat, CapabilityKinds.Embed, CapabilityKinds.Rerank, CapabilityKinds.LlamaCpp], backend.Kinds);
        Assert.True(backend.SupportsModelManagement);
    }

    [Fact]
    public async Task AModelStillDownloadingIsNotListed()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Models["qwen"] = ("loaded", "models_dir", false);
        router.Models["owner/repo:Q4"] = ("downloading", "cache", true);

        var models = await backend.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["qwen"], models!.Select(m => m.Name).ToArray());
    }

    [Fact]
    public async Task APresetsOwnWeightsAreNotListedASecondTimeUnderAnotherName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "models");
        var (backend, router) = Backend(Router(e =>
        {
            e.Serve.ModelsDir = dir;
            e.Serve.Presets["jina-rerank"] = new LlamaCppPresetOptions { HfRepo = "gpustack/jina-reranker-v1-tiny-en-GGUF:Q8_0", Reranking = true };
            e.Serve.Presets["nomic"] = new LlamaCppPresetOptions { Model = Path.Combine(dir, "nomic-embed.gguf"), Embeddings = true };
        }));

        // What b11417 listed in the live run once the reranker had been downloaded.
        router.Models["jina-rerank"] = ("unloaded", "preset", false);
        router.Models["gpustack/jina-reranker-v1-tiny-en-GGUF:Q8_0"] = ("unloaded", "cache", true);
        router.Models["nomic"] = ("unloaded", "preset", false);
        router.Models["nomic-embed"] = ("unloaded", "models_dir", false);
        router.Models["qwen"] = ("unloaded", "models_dir", false);

        var models = await backend.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["jina-rerank", "nomic", "qwen"], models!.Select(m => m.Name).Order().ToArray());
    }

    [Fact]
    public async Task ARouterThatDoesNotAnswerIsCouldNotAskNotHasNone()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Down = true;

        Assert.Null(await backend.ListModelsAsync(CancellationToken.None));
    }

    // ---- D3: model management ------------------------------------------------------------

    [Fact]
    public async Task APullStartsTheRoutersDownloadAndEndsWhenItIsNoLongerDownloading()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.OnPull = name => router.Models[name] = ("downloading", "cache", true);
        router.AfterPolls(2, () => router.Models["bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M"] = ("unloaded", "cache", true));

        var frames = new List<ModelPullProgress>();
        await foreach (var frame in backend.PullAsync("hf.co/bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M", CancellationToken.None))
        {
            frames.Add(frame);
        }

        // Ollama's hf.co/ spelling of the same repo is stripped; llama.cpp takes it bare.
        Assert.Contains("POST /models {\"model\":\"bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M\"}", router.Calls);
        var frame0 = Assert.Single(frames);
        Assert.Null(frame0.Total);
        Assert.Contains("no byte counts", frame0.Status);
    }

    [Fact]
    public async Task ADownloadTheRouterDroppedIsAFailureThatPointsAtTheLog()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.OnPull = name => router.Models[name] = ("downloading", "cache", true);
        router.AfterPolls(1, () => router.Models.Clear());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in backend.PullAsync("owner/missing:Q4", CancellationToken.None))
            {
            }
        });

        Assert.Contains("this node's log", failure.Message);
    }

    [Theory]
    [InlineData("llama3.2")]
    [InlineData("owner/repo/extra")]
    [InlineData("hf.co/")]
    public async Task APullOfSomethingThatIsNotARepoIsRefusedBeforeAnyRequest(string name)
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in backend.PullAsync(name, CancellationToken.None))
            {
            }
        });

        Assert.Empty(router.Calls);
    }

    [Fact]
    public async Task OnlyWhatTheRouterCanRemoveIsDeletedAndAFileOnTheBoxIsRefused()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Models["qwen"] = ("unloaded", "models_dir", false);
        router.Models["owner/repo:Q4"] = ("unloaded", "cache", true);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.DeleteAsync("qwen", CancellationToken.None));
        Assert.Contains("Serve:ModelsDir", refusal.Message);

        await backend.DeleteAsync("owner/repo:Q4", CancellationToken.None);
        Assert.Contains("DELETE /models?model=owner%2Frepo%3AQ4", router.Calls);
    }

    [Fact]
    public async Task AWarmLoadsAndWaitsForLoaded()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Models["qwen"] = ("unloaded", "models_dir", false);
        router.OnLoad = name => router.Models[name] = ("loading", "models_dir", false);
        router.AfterPolls(2, () => router.Models["qwen"] = ("loaded", "models_dir", false));

        await backend.WarmAsync("qwen", CancellationToken.None);

        Assert.Contains("POST /models/load {\"model\":\"qwen\"}", router.Calls);
    }

    [Fact]
    public async Task AModelThatFailsToLoadIsAFailedWarmNotATimeout()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Models["qwen"] = ("unloaded", "models_dir", false);
        router.OnLoad = name => router.Models[name] = ("loading", "models_dir", false);
        router.AfterPolls(1, () => router.Models["qwen"] = ("unloaded", "models_dir", false));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.WarmAsync("qwen", CancellationToken.None));
        Assert.Contains("could not load", failure.Message);
    }

    [Fact]
    public async Task AnUnloadFreesALoadedModelAndLeavesAnUnloadedOneAlone()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Models["qwen"] = ("loaded", "models_dir", false);
        router.Models["smol"] = ("unloaded", "models_dir", false);

        await backend.UnloadAsync("qwen", CancellationToken.None);
        await backend.UnloadAsync("smol", CancellationToken.None);

        Assert.Equal(["POST /models/unload {\"model\":\"qwen\"}"], router.Calls.Where(c => c.Contains("unload")).ToArray());
    }

    [Fact]
    public async Task OneModelFixedAtLaunchManagesNothingAndSaysWhatCould()
    {
        var (backend, _) = Backend(new EngineOptions { Type = "llamacpp", Serve = { Model = "/m/q.gguf" } }, router: false);

        Assert.False(backend.SupportsModelManagement);
        var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => backend.WarmAsync("q", CancellationToken.None));
        Assert.Contains("Serve:ModelsDir", refusal.Message);
    }

    [Fact]
    public async Task TwoEnginesThatCanPullAndNoNameIsARefusalNamingBoth()
    {
        var (llama, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Models["qwen"] = ("unloaded", "models_dir", false);
        var ollama = new ManagingFake("llama3:latest");
        var multi = new MultiBackend(
            [Running(new Engine("gguf", "llamacpp", llama, true)), Running(new Engine("ollama", "ollama", ollama, true))],
            TimeSpan.FromSeconds(1),
            TimeProvider.System,
            NullLogger.Instance);
        await multi.ListModelsAsync(CancellationToken.None);

        var refusal = Assert.Throws<InvalidOperationException>(() => multi.Manager(ModelCommand.KindPull, "owner/repo:Q4", null));
        Assert.Contains("gguf, ollama", refusal.Message);
        Assert.Contains("?engine=", refusal.Message);

        Assert.Same(llama, multi.Manager(ModelCommand.KindPull, "owner/repo:Q4", "GGUF").Backend);

        // Anything but a pull goes where the model is.
        Assert.Same(llama, multi.Manager(ModelCommand.KindUnload, "qwen", null).Backend);
        Assert.Same(ollama, multi.Manager(ModelCommand.KindWarm, "llama3", null).Backend);
    }

    [Fact]
    public void ANodeSaysItCanManageModelsBeforeItsEnginesHaveStarted()
    {
        // Found live: a meshed node registers before its engines start, and the hub reads this once.
        var (llama, _) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        var multi = new MultiBackend([new Engine("gguf", "llamacpp", llama, true)], TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger.Instance);

        Assert.True(multi.SupportsModelManagement);
        Assert.Contains("is stopped", Assert.Throws<InvalidOperationException>(() => multi.Manager(ModelCommand.KindPull, "o/r:Q4", "gguf")).Message);
        Assert.Contains("no running engine", Assert.Throws<NotSupportedException>(() => multi.Manager(ModelCommand.KindPull, "o/r:Q4", null)).Message);
    }

    [Fact]
    public async Task AnUnloadCommandEndsUnloaded()
    {
        var ollama = new ManagingFake("llama3:latest");
        var executor = new ModelCommandExecutor(ollama, NullLogger<ModelCommandExecutor>.Instance);

        var frames = new List<ModelCommandProgress>();
        await foreach (var frame in executor.ExecuteAsync(new ModelCommand(Guid.NewGuid(), ModelCommand.KindUnload, "llama3"), "n", CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(["unloading", "unloaded"], frames.Select(f => f.Status).ToArray());
        Assert.Equal(["unload:llama3"], ollama.Calls);
    }

    [Fact]
    public async Task ACommandNamingAnEngineOnANodeWithoutEnginesIsRefused()
    {
        var executor = new ModelCommandExecutor(new ManagingFake("llama3"), NullLogger<ModelCommandExecutor>.Instance);

        var frames = new List<ModelCommandProgress>();
        await foreach (var frame in executor.ExecuteAsync(new ModelCommand(Guid.NewGuid(), ModelCommand.KindPull, "x", Engine: "gguf"), "n", CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Contains("not engines", Assert.Single(frames).Error);
    }

    // ---- D4: the native routes -----------------------------------------------------------

    [Fact]
    public async Task ANativeCallSendsTheCallersBodyToTheRouteAndReturnsTheAnswerUntouched()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Native["/tokenize"] = (200, """{"tokens":[14990,1879]}""");

        var body = """{"model":"qwen","content":"hello world","with_pieces":true}""";
        var result = await backend.RunAsync(Job(CapabilityKinds.LlamaCpp, "qwen", LlamaCppNative.Payload("tokenize", body)), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("""{"tokens":[14990,1879]}""", result.Payload);
        Assert.Contains($"POST /tokenize {body}", router.Calls);
    }

    [Fact]
    public async Task PropsIsAGetNamingTheModel()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Native["/props"] = (200, """{"n_ctx":4096}""");

        var result = await backend.RunAsync(Job(CapabilityKinds.LlamaCpp, "owner/repo:Q4", LlamaCppNative.Payload("props", null)), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("GET /props?model=owner%2Frepo%3AQ4", router.Calls);
    }

    [Theory]
    [InlineData(400, """{"error":{"code":400,"message":"model 'zzz' not found"}}""", BrioErrorCodes.ModelNotFound)]
    [InlineData(400, """{"error":{"code":400,"message":"\"content\" must be provided"}}""", ToolErrorCodes.InvalidRequest)]
    [InlineData(500, """{"error":{"code":500,"message":"model name=x failed to load"}}""", null)]
    public async Task TheEnginesRefusalKeepsItsSentenceAndIsStatedAsAKind(int status, string answer, string? code)
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Native["/infill"] = (status, answer);

        var result = await backend.RunAsync(Job(CapabilityKinds.LlamaCpp, "zzz", LlamaCppNative.Payload("infill", """{"model":"zzz"}""")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.DoesNotContain("{", result.Error);
    }

    [Fact]
    public async Task ABusyEngineIsARetryNotAFailure()
    {
        var (backend, router) = Backend(Router(e => e.Serve.ModelsDir = "/models"));
        router.Native["/completion"] = (503, """{"error":{"code":503,"message":"Loading model"}}""");

        var result = await backend.RunAsync(Job(CapabilityKinds.LlamaCpp, "q", LlamaCppNative.Payload("completion", """{"model":"q"}""")), CancellationToken.None);

        Assert.NotNull(result.RetryAfterSeconds);
    }

    // ---- D5: rerank ------------------------------------------------------------------------

    [Fact]
    public async Task ARerankJobIsAnsweredWithOneScorePerDocumentInDocumentOrder()
    {
        var (backend, router) = Backend(Router(e =>
            e.Serve.Presets["bge"] = new LlamaCppPresetOptions { Model = "/m/b.gguf", Reranking = true }));
        router.Native["/v1/rerank"] = (200, """{"results":[{"index":1,"relevance_score":0.2},{"index":0,"relevance_score":1.5}],"usage":{"total_tokens":11}}""");

        Assert.True(backend.Serves(CapabilityKinds.Rerank, "bge"));
        Assert.False(backend.Serves(CapabilityKinds.Rerank, "qwen"));

        var result = await backend.RunAsync(Job(CapabilityKinds.Rerank, "bge", """{"query":"cat","documents":["a cat","stocks"]}"""), CancellationToken.None);

        Assert.True(result.Success);
        using var document = JsonDocument.Parse(result.Payload!);
        Assert.Equal([1.5, 0.2], document.RootElement.GetProperty("scores").EnumerateArray().Select(e => e.GetDouble()).ToArray());
        Assert.Equal(11, document.RootElement.GetProperty("total_tokens").GetInt64());
        Assert.Contains("POST /v1/rerank {\"model\":\"bge\",\"query\":\"cat\",\"documents\":[\"a cat\",\"stocks\"]}", router.Calls);
    }

    [Fact]
    public async Task AReRankerThatSkippedADocumentFailsRatherThanScoringItZero()
    {
        var (backend, router) = Backend(Router(e =>
            e.Serve.Presets["bge"] = new LlamaCppPresetOptions { Model = "/m/b.gguf", Reranking = true }));
        router.Native["/v1/rerank"] = (200, """{"results":[{"index":0,"relevance_score":1.5}]}""");

        var result = await backend.RunAsync(Job(CapabilityKinds.Rerank, "bge", """{"query":"q","documents":["a","b"]}"""), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("1 of 2", result.Error);
    }

    // ---- composition ---------------------------------------------------------------------

    [Fact]
    public void ARouterEngineComposesAsAManagingLlamaCppBackendAndTheNodeAnswersItsJobs()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Coordinator:Url"] = "http://localhost:5080/",
            ["Backend:Engines:gguf:Type"] = "llamacpp",
            ["Backend:Engines:gguf:Serve:ModelsDir"] = Path.GetTempPath(),
            ["Backend:Engines:gguf:Serve:Port"] = "8099",
            ["Backend:Engines:gguf:Autostart"] = "false"
        });
        builder.AddInferHubNode();
        using var host = builder.Build();

        var multi = Assert.IsType<MultiBackend>(host.Services.GetRequiredService<IInferenceBackend>());
        var engine = Assert.IsType<LlamaCppBackend>(multi.Engines.Single().Backend);
        Assert.True(engine.IsRouter);
        Assert.Equal("http://127.0.0.1:8099/v1", engine.Endpoint);
        Assert.Same(multi, host.Services.GetRequiredService<IBackendToolJobs>());
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/")]
    [InlineData("http://box:9000/v1/", "http://box:9000/")]
    [InlineData("http://box:9000", "http://box:9000/")]
    public void TheNativeRoutesLiveAtTheServersRoot(string baseUrl, string root)
        => Assert.Equal(root, MultiBackendComposition.RootOf(baseUrl)!.ToString());

    // ---- helpers -------------------------------------------------------------------------

    private static EngineOptions Router(Action<EngineOptions> configure)
    {
        var engine = new EngineOptions { Type = "llamacpp" };
        configure(engine);
        return engine;
    }

    private static (LlamaCppBackend Backend, StubRouter Router) Backend(EngineOptions engine, bool router = true)
    {
        var stub = new StubRouter();
        var backend = new LlamaCppBackend(
            new NoUpstream(),
            engine,
            router,
            () => new HttpClient(stub, disposeHandler: false) { BaseAddress = new Uri("http://127.0.0.1:8080/") },
            new FastTime(),
            NullLogger.Instance);

        return (backend, stub);
    }

    private static ToolJob Job(string capability, string model, string payload) => new(Guid.NewGuid(), capability, model, payload);

    private static Engine Running(Engine engine)
    {
        engine.SetRunning(true);
        return engine;
    }

    private static void Validate(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = configuration.GetSection(BackendOptions.SectionName).Get<BackendOptions>() ?? new BackendOptions();
        var result = new BackendOptionsValidator(configuration).Validate(null, options);

        if (result.Failed)
        {
            throw new OptionsValidationException("Backend", typeof(BackendOptions), result.Failures!);
        }
    }

    /// <summary>Polls without waiting: a second here is a tick.</summary>
    private sealed class FastTime : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            now += dueTime;
            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>b11417's router wire, as measured: <c>/models</c>, load, unload, pull, delete, and native routes.</summary>
    private sealed class StubRouter : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> calls = new();
        private int polls;
        private (int After, Action Change)? scheduled;

        public ConcurrentDictionary<string, (string Status, string Source, bool CanRemove)> Models { get; } = new();

        public Dictionary<string, (int Status, string Body)> Native { get; } = new();

        public Action<string>? OnPull { get; set; }

        public Action<string>? OnLoad { get; set; }

        public bool Down { get; set; }

        public IReadOnlyList<string> Calls => calls.ToArray();

        public void AfterPolls(int count, Action change) => scheduled = (count, change);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("connection refused");
            }

            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            if (request.Method == HttpMethod.Get && path == "/models")
            {
                if (scheduled is { } s && ++polls > s.After)
                {
                    scheduled = null;
                    s.Change();
                }

                return Json(200, JsonSerializer.Serialize(new
                {
                    data = Models.Select(m => new { id = m.Key, status = new { value = m.Value.Status }, source = m.Value.Source, can_remove = m.Value.CanRemove })
                }));
            }

            calls.Enqueue(body is null ? $"{request.Method} {path}" : $"{request.Method} {path} {body}");
            var model = body is null ? null : JsonDocument.Parse(body).RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;

            switch (request.Method.Method, path)
            {
                case ("POST", "/models"):
                    OnPull?.Invoke(model!);
                    return Json(200, """{"success":true}""");
                case ("POST", "/models/load"):
                    OnLoad?.Invoke(model!);
                    return Json(200, """{"success":true}""");
                case ("POST", "/models/unload"):
                case ("DELETE", _) when path.StartsWith("/models?model=", StringComparison.Ordinal):
                    return Json(200, """{"success":true}""");
            }

            var route = path.Split('?')[0];
            return Native.TryGetValue(route, out var answer) ? Json(answer.Status, answer.Body) : Json(404, """{"error":{"message":"File Not Found"}}""");
        }

        private static HttpResponseMessage Json(int status, string body)
            => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class NoUpstream : IInferenceBackend
    {
        public string Name => "llamacpp";

        public string Endpoint => "http://127.0.0.1:8080/v1";

        public IReadOnlyList<string> Kinds => [CapabilityKinds.Chat];

        public bool SupportsModelManagement => false;

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>?>([new ModelInfo("q", null, null)]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public async IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return "{}";
        }

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>An Ollama-like engine that manages models and records what it was asked.</summary>
    private sealed class ManagingFake(string model) : IInferenceBackend
    {
        private readonly ConcurrentQueue<string> calls = new();

        public IReadOnlyList<string> Calls => calls.ToArray();

        public string Name => "ollama";

        public string Endpoint => "http://localhost:11434";

        public IReadOnlyList<string> Kinds => [CapabilityKinds.Chat];

        public bool SupportsModelManagement => true;

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>?>([new ModelInfo(model, null, null)]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult("{}");

        public async IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return "{}";
        }

        public async IAsyncEnumerable<ModelPullProgress> PullAsync(string name, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            calls.Enqueue($"pull:{name}");
            await Task.CompletedTask;
            yield return new ModelPullProgress("success", null, null);
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            calls.Enqueue($"delete:{name}");
            return Task.CompletedTask;
        }

        public Task WarmAsync(string name, CancellationToken cancellationToken)
        {
            calls.Enqueue($"warm:{name}");
            return Task.CompletedTask;
        }

        public Task UnloadAsync(string name, CancellationToken cancellationToken)
        {
            calls.Enqueue($"unload:{name}");
            return Task.CompletedTask;
        }
    }
}
