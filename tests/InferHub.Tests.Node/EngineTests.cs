using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using InferHub.Node;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Supervision;
using InferHub.Node.Capabilities;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 95: several engines on one node — ollama, llama.cpp and colibri side by side — routed by
/// model, started and stopped in place, and clamped by the node's own list. The live run against a
/// real Ollama, a real <c>llama-server</c> and a real colibri is in the release notes.
/// </summary>
public class EngineTests
{
    private static MultiBackend Multi(TimeSpan? drain = null, params Engine[] engines)
        => new(engines, drain ?? TimeSpan.FromSeconds(5), TimeProvider.System, NullLogger.Instance);

    private static Engine Running(Engine engine)
    {
        engine.SetRunning(true);
        return engine;
    }

    // ---- D1: one backend in front, routed by model ----------------------------------------

    [Fact]
    public async Task EachRequestGoesToTheEngineThatReportedItsModel()
    {
        var ollama = new FakeEngine("ollama-model", [CapabilityKinds.Chat, CapabilityKinds.Embed]);
        var llama = new FakeEngine("qwen-gguf", [CapabilityKinds.Chat]);
        var backend = Multi(null,
            Running(new Engine("ollama", BackendOptions.Ollama, ollama, autostart: true)),
            Running(new Engine("qwen", BackendOptions.LlamaCpp, llama, autostart: true)));

        var models = await backend.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["ollama-model", "qwen-gguf"], models!.Select(m => m.Name).Order().ToArray());

        await backend.ChatAsync("""{"model":"qwen-gguf","messages":[]}""", CancellationToken.None);
        await backend.EmbedAsync("""{"model":"ollama-model","input":"x"}""", CancellationToken.None);
        await foreach (var _ in backend.StreamAsync("chat", """{"model":"qwen-gguf","messages":[]}""", CancellationToken.None))
        {
        }

        Assert.Equal(["chat:qwen-gguf", "stream:qwen-gguf"], llama.Calls);
        Assert.Equal(["embed:ollama-model"], ollama.Calls);
    }

    [Fact]
    public async Task ARequestBeforeTheFirstListingAsksOnceRatherThanFailing()
    {
        var llama = new FakeEngine("qwen-gguf", [CapabilityKinds.Chat]);
        var backend = Multi(null, Running(new Engine("qwen", BackendOptions.LlamaCpp, llama, autostart: true)));

        await backend.ChatAsync("""{"model":"qwen-gguf"}""", CancellationToken.None);

        Assert.Equal(["chat:qwen-gguf"], llama.Calls);
    }

    [Fact]
    public async Task OllamasLatestTagIsTheSameModelEitherWayRound()
    {
        var ollama = new FakeEngine("llama3:latest", [CapabilityKinds.Chat]);
        var backend = Multi(null, Running(new Engine("ollama", BackendOptions.Ollama, ollama, autostart: true)));
        await backend.ListModelsAsync(CancellationToken.None);

        await backend.ChatAsync("""{"model":"llama3"}""", CancellationToken.None);

        Assert.Equal(["chat:llama3"], ollama.Calls);
    }

    [Fact]
    public async Task AModelTwoEnginesReportGoesToTheOneListedFirst()
    {
        var first = new FakeEngine("shared", [CapabilityKinds.Chat]);
        var second = new FakeEngine("shared", [CapabilityKinds.Chat]);
        var backend = Multi(null,
            Running(new Engine("a", BackendOptions.OpenAi, first, autostart: true)),
            Running(new Engine("b", BackendOptions.OpenAi, second, autostart: true)));

        Assert.Single((await backend.ListModelsAsync(CancellationToken.None))!);
        await backend.ChatAsync("""{"model":"shared"}""", CancellationToken.None);

        Assert.Single(first.Calls);
        Assert.Empty(second.Calls);
    }

    [Fact]
    public async Task AModelNoRunningEngineServesIsRefusedNamingIt()
    {
        var backend = Multi(null, Running(new Engine("qwen", BackendOptions.LlamaCpp, new FakeEngine("qwen-gguf", [CapabilityKinds.Chat]), autostart: true)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => backend.ChatAsync("""{"model":"llama3:70b"}""", CancellationToken.None));

        Assert.Contains("no running engine on this node serves 'llama3:70b'", ex.Message);
    }

    // ---- 69's rule, per engine ----------------------------------------------------------

    [Fact]
    public async Task OneDeadEngineWithdrawsOnlyItsOwnModelsAndAllDeadIsNotZero()
    {
        var ollama = new FakeEngine("ollama-model", [CapabilityKinds.Chat]);
        var llama = new FakeEngine("qwen-gguf", [CapabilityKinds.Chat]) { Down = true };
        var backend = Multi(null,
            Running(new Engine("ollama", BackendOptions.Ollama, ollama, autostart: true)),
            Running(new Engine("qwen", BackendOptions.LlamaCpp, llama, autostart: true)));

        var models = await backend.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["ollama-model"], models!.Select(m => m.Name).ToArray());

        var state = backend.State("n").Engines;
        Assert.Equal(NodeEngineInfo.Running, state.Single(e => e.Name == "ollama").State);
        Assert.Equal(NodeEngineInfo.Starting, state.Single(e => e.Name == "qwen").State);

        // Once it has answered, a later silence is a lost engine, not a loading one.
        llama.Down = false;
        await backend.ListModelsAsync(CancellationToken.None);
        llama.Down = true;
        await backend.ListModelsAsync(CancellationToken.None);
        Assert.Equal(NodeEngineInfo.Unreachable, backend.State("n").Engines.Single(e => e.Name == "qwen").State);

        // "Could not ask" is not "has none": every engine down is null, not an empty inventory.
        ollama.Down = true;
        Assert.Null(await backend.ListModelsAsync(CancellationToken.None));
    }

    // ---- D4/D5: start and stop in place --------------------------------------------------

    [Fact]
    public async Task AutostartDecidesTheBootAndAProfileOverridesItEitherWay()
    {
        var ollama = new FakeEngine("ollama-model", [CapabilityKinds.Chat]);
        var colibri = new FakeEngine("olmoe", [CapabilityKinds.Chat, CapabilityKinds.Score]);
        var backend = Multi(null,
            new Engine("ollama", BackendOptions.Ollama, ollama, autostart: true),
            new Engine("colibri", BackendOptions.Colibri, colibri, autostart: false));

        Assert.Equal(["backend 'ollama' started"], await backend.StartAsync(CancellationToken.None));
        Assert.Equal(["ollama-model"], (await backend.ListModelsAsync(CancellationToken.None))!.Select(m => m.Name).ToArray());

        var changes = await backend.ApplyAsync(
            new Dictionary<string, bool> { ["colibri"] = true, ["ollama"] = false },
            CancellationToken.None);

        Assert.Equal(["backend 'ollama' stopped", "backend 'colibri' started"], changes);
        Assert.Equal(["olmoe"], (await backend.ListModelsAsync(CancellationToken.None))!.Select(m => m.Name).ToArray());
        Assert.Equal(NodeEngineInfo.Stopped, backend.State("n").Engines.Single(e => e.Name == "ollama").State);
        Assert.Empty(backend.State("n").Engines.Single(e => e.Name == "ollama").Models);

        // No profile any more: back to what the box boots as.
        Assert.Equal(["backend 'ollama' started", "backend 'colibri' stopped"], await backend.ApplyAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task TheBootConvergesOnAProfileThatArrivedFirst()
    {
        var backend = Multi(null,
            new Engine("ollama", BackendOptions.Ollama, new FakeEngine("m", [CapabilityKinds.Chat]), autostart: true));

        await backend.ApplyAsync(new Dictionary<string, bool> { ["ollama"] = false }, CancellationToken.None);

        Assert.Empty(await backend.StartAsync(CancellationToken.None));
        Assert.False(backend.Engines.Single().Running);
    }

    [Fact]
    public async Task AStartedEngineIsAnnouncedTheMomentItAnswers()
    {
        var llama = new FakeEngine("qwen-gguf", [CapabilityKinds.Chat]);
        var backend = Multi(null, new Engine("qwen", BackendOptions.LlamaCpp, llama, autostart: false));
        var changed = new TaskCompletionSource();
        backend.Changed += () => changed.TrySetResult();

        await backend.ApplyAsync(new Dictionary<string, bool> { ["qwen"] = true }, CancellationToken.None);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NodeEngineInfo.Running, backend.State("n").Engines.Single().State);
        Assert.Equal(["qwen-gguf"], backend.State("n").Engines.Single().Models);
    }

    [Fact]
    public async Task AStoppedEngineTakesNoNewWorkAndItsInFlightRequestFinishes()
    {
        var gate = new TaskCompletionSource();
        var llama = new FakeEngine("qwen-gguf", [CapabilityKinds.Chat]) { Gate = gate.Task };
        var backend = Multi(TimeSpan.FromSeconds(10), Running(new Engine("qwen", BackendOptions.LlamaCpp, llama, autostart: true)));
        await backend.ListModelsAsync(CancellationToken.None);

        var inFlight = backend.ChatAsync("""{"model":"qwen-gguf"}""", CancellationToken.None);
        await WaitUntil(() => backend.Engines.Single().InFlight == 1);

        var stopping = backend.ApplyAsync(new Dictionary<string, bool> { ["qwen"] = false }, CancellationToken.None);

        // D5: out of the routing table before the drain, so a second request is refused at once…
        await WaitUntil(() => !backend.Engines.Single().Running);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => backend.ChatAsync("""{"model":"qwen-gguf"}""", CancellationToken.None));

        // …while the one already running is allowed to finish, and the stop waits for it.
        Assert.False(stopping.IsCompleted);
        gate.SetResult();
        Assert.Equal("{}", await inFlight);
        Assert.Equal(["backend 'qwen' stopped"], await stopping);
    }

    [Fact]
    public async Task StoppingOllamaUnloadsWhatThisNodeLoadedAndNothingElse()
    {
        var unloaded = new List<string>();
        var ollama = new FakeEngine("mine", [CapabilityKinds.Chat], extra: "owners-other-model");
        var backend = Multi(null, Running(new Engine(
            "ollama", BackendOptions.Ollama, ollama, autostart: true,
            unload: (models, _) => { unloaded.AddRange(models); return Task.CompletedTask; })));
        await backend.ListModelsAsync(CancellationToken.None);

        await backend.ChatAsync("""{"model":"mine"}""", CancellationToken.None);
        await backend.ApplyAsync(new Dictionary<string, bool> { ["ollama"] = false }, CancellationToken.None);

        Assert.Equal(["mine"], unloaded);
    }

    // ---- per-model kinds, and Brio -------------------------------------------------------

    [Fact]
    public async Task EachModelIsDeclaredUnderItsOwnEnginesKindsOnly()
    {
        var backend = Multi(null,
            Running(new Engine("ollama", BackendOptions.Ollama, new FakeEngine("llama3", [CapabilityKinds.Chat, CapabilityKinds.Embed]), autostart: true)),
            Running(new Engine("colibri", BackendOptions.Colibri, new FakeEngine("olmoe", [CapabilityKinds.Chat, CapabilityKinds.Score]), autostart: true)),
            Running(new Engine("emb", BackendOptions.LlamaCpp, new FakeEngine("nomic-gguf", [CapabilityKinds.Embed]), autostart: true)));

        var models = await backend.ListModelsAsync(CancellationToken.None);
        var declared = BackendCapabilities.Declare(models!, backend.Kinds, new CapabilityOptions(), modelKinds: backend)
            .ToDictionary(c => c.Kind, c => c.Models.ToArray());

        Assert.Equal(["llama3", "olmoe"], declared[CapabilityKinds.Chat]);
        Assert.Equal(["llama3", "nomic-gguf"], declared[CapabilityKinds.Embed]);
        Assert.Equal(["olmoe"], declared[CapabilityKinds.Score]);
    }

    [Fact]
    public async Task ScoringGoesToTheColibriEngineAndIsRefusedWhenItIsStopped()
    {
        var colibri = new FakeEngine("olmoe", [CapabilityKinds.Chat, CapabilityKinds.Score]);
        var backend = Multi(null,
            Running(new Engine("llama", BackendOptions.LlamaCpp, new FakeEngine("qwen", [CapabilityKinds.Chat]), autostart: true)),
            Running(new Engine("colibri", BackendOptions.Colibri, colibri, autostart: true)));

        var job = new ToolJob(Guid.NewGuid(), CapabilityKinds.Score, "olmoe", "{}");
        Assert.True((await backend.ScoreAsync(job, CancellationToken.None)).Success);
        Assert.Equal(["score:olmoe"], colibri.Calls);

        await backend.ApplyAsync(new Dictionary<string, bool> { ["colibri"] = false }, CancellationToken.None);
        var refused = await backend.ScoreAsync(job, CancellationToken.None);
        Assert.False(refused.Success);
        Assert.Contains("no colibri engine is running", refused.Error);
    }

    // ---- D4: the clamp — the node's list is the ceiling ----------------------------------

    [Fact]
    public void AProfileStartsAndStopsOnlyEnginesTheBoxListed()
    {
        var ceiling = TestProfiles.OpenCeiling() with { EngineNames = ["ollama", "qwen"] };
        var result = NodeProfileClamp.Apply(ceiling, TestProfiles.Profile(
            name: "p",
            selector: new NodeProfileSelector(NodeId: "n")) with
        {
            Backends = new Dictionary<string, bool>
            {
                ["QWEN"] = true,
                ["ollama"] = false,
                ["/usr/bin/llama-server -m /etc/shadow"] = true,
                ["never-heard-of"] = false
            }
        });

        Assert.Equal(new Dictionary<string, bool> { ["qwen"] = true, ["ollama"] = false }, result.Effective.Backends);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("backend:/usr/bin/llama-server -m /etc/shadow", refusal.Item);
        Assert.Contains("Backend:Engines on this node does not name", refusal.Reason);
        Assert.Contains("backend 'never-heard-of' off", result.Applied);
    }

    [Fact]
    public void ASingleBackendNodeRefusesEveryEngineInstruction()
    {
        var result = NodeProfileClamp.Apply(TestProfiles.OpenCeiling(), TestProfiles.Profile(
            name: "p",
            selector: new NodeProfileSelector(NodeId: "n")) with
        {
            Backends = new Dictionary<string, bool> { ["ollama"] = false }
        });

        Assert.Empty(result.Effective.Backends!);
        Assert.Contains("runs a single backend", Assert.Single(result.Refusals).Reason);
    }

    [Fact]
    public void NoBackendsInTheProfileLeavesEveryEngineToItsAutostart()
    {
        var ceiling = TestProfiles.OpenCeiling() with { EngineNames = ["ollama"] };
        var result = NodeProfileClamp.Apply(ceiling, TestProfiles.Profile(name: "p", selector: new NodeProfileSelector(NodeId: "n")));

        Assert.Null(result.Effective.Backends);
    }

    // ---- D2/D3: configuration ------------------------------------------------------------

    [Fact]
    public void ANodeWithEnginesComposesOneMultiBackendAndNoSingleBackendHealth()
    {
        using var host = Build(new()
        {
            ["Backend:Engines:ollama:Type"] = "ollama",
            ["Backend:Engines:qwen:Type"] = "llamacpp",
            ["Backend:Engines:qwen:BaseUrl"] = "http://127.0.0.1:8080/v1",
            ["Backend:Engines:colibri:Type"] = "colibri",
            ["Backend:Engines:colibri:Autostart"] = "false"
        });

        var backend = Assert.IsType<MultiBackend>(host.Services.GetRequiredService<IInferenceBackend>());
        // Sorted by name: configuration binds a section's keys that way, and it is the collision tie-break.
        Assert.Equal(["colibri", "ollama", "qwen"], backend.EngineNames);
        Assert.Same(backend, host.Services.GetRequiredService<IEngineControl>());
        Assert.Same(backend, host.Services.GetRequiredService<IClosedSetScorer>());

        // D6: phase 69 routes a whole node on one health verdict, and one engine down must not
        // unroute the other two.
        Assert.IsType<NoBackendSupervisor>(host.Services.GetRequiredService<IBackendSupervisor>());

        var kinds = backend.Engines.ToDictionary(e => e.Name, e => e.Backend.Kinds.ToArray());
        Assert.Equal([CapabilityKinds.Chat], kinds["qwen"]);
        Assert.Equal([CapabilityKinds.Chat, CapabilityKinds.Score], kinds["colibri"]);
        Assert.Equal("http://127.0.0.1:8080/v1", backend.Engines.Single(e => e.Name == "qwen").Backend.Endpoint);
        Assert.Equal("http://127.0.0.1:8000/v1", backend.Engines.Single(e => e.Name == "colibri").Backend.Endpoint);
    }

    [Fact]
    public void ANodeWithoutEnginesIsTheSingleBackendNodeItWas()
    {
        using var host = Build(new());

        Assert.IsNotType<MultiBackend>(host.Services.GetRequiredService<IInferenceBackend>());
        Assert.Null(host.Services.GetService<IEngineControl>());
        Assert.Null(host.Services.GetService<IClosedSetScorer>());
    }

    [Theory]
    [InlineData("Backend:Type", "colibri", "replaces the single backend type")]
    [InlineData("Backend:Engines:x:Type", "anthropic", "A cloud vendor is not an engine")]
    [InlineData("Backend:Engines:other:Type", "ollama", "names 2 ollama engines")]
    [InlineData("Backend:Engines:../etc:Type", "llamacpp", "an engine name is letters")]
    [InlineData("Backend:Engines:remote:Type", "openai", "BaseUrl must be set for an openai engine")]
    [InlineData("Backend:Engines:ollama:BaseUrl", "http://elsewhere:11434", "its address is Ollama:Endpoint")]
    public void ABadEngineListIsAStartupFailureNamingTheKey(string key, string value, string expected)
    {
        var settings = new Dictionary<string, string?> { ["Backend:Engines:ollama:Type"] = "ollama", [key] = value };

        var ex = Assert.Throws<OptionsValidationException>(
            () => Build(settings).Services.GetRequiredService<IOptions<BackendOptions>>().Value);

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void TwoLaunchedEnginesOnOnePortAndAPromptLoggingFlagAreRefused()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backend:Engines:a:Type"] = "llamacpp",
            ["Backend:Engines:a:Serve:Model"] = "/models/a.gguf",
            ["Backend:Engines:b:Type"] = "llamacpp",
            ["Backend:Engines:b:Serve:Model"] = "/models/b.gguf",
            ["Backend:Engines:b:Serve:Arguments:0"] = "--verbose"
        };

        var ex = Assert.Throws<OptionsValidationException>(
            () => Build(settings).Services.GetRequiredService<IOptions<BackendOptions>>().Value);

        Assert.Contains("would both launch an engine on port 8080", ex.Message);
        Assert.Contains("'--verbose', which makes llama-server log prompts", ex.Message);
    }

    [Fact]
    public void ALaunchedLlamaServerListensOnLoopbackUnderItsAlias()
    {
        var engine = new EngineOptions
        {
            Type = "llamacpp",
            Embeddings = true,
            Serve = new EngineServeOptions { Executable = "/opt/llama/llama-server", Model = "/models/nomic-embed.Q8_0.gguf", Port = 8091, Arguments = ["-c", "2048"] }
        };

        var info = LlamaCppServe.StartInfo(engine);

        Assert.Equal("/opt/llama/llama-server", info.FileName);
        Assert.Equal(
            ["-m", "/models/nomic-embed.Q8_0.gguf", "--alias", "nomic-embed.Q8_0", "--host", "127.0.0.1", "--port", "8091", "--embeddings", "-c", "2048"],
            info.ArgumentList.ToArray());
        Assert.False(info.UseShellExecute);
        Assert.Equal("http://127.0.0.1:8091/v1", engine.Serve.LaunchedBaseUrl());
    }

    // ---- D3: the launched process --------------------------------------------------------

    [Fact]
    public async Task ALaunchedEngineIsStoppedWithItsWholeProcessAndAMissingModelIsOneFailure()
    {
        var process = new EngineProcess(
            "sleeper",
            () => OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("ping", "-n 60 127.0.0.1") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }
                : new System.Diagnostics.ProcessStartInfo("sleep", "60") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true },
            () => null,
            TimeProvider.System,
            NullLogger.Instance);

        process.Start();
        Assert.True(process.IsRunning);

        var stop = process.StopAsync();
        Assert.True(await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15))) == stop, "the stop did not return: the child outlived it");
        Assert.False(process.IsRunning);

        var missing = new EngineProcess("missing", () => throw new InvalidOperationException("never launched"), () => "Serve:Model '/nope.gguf' is not a file", TimeProvider.System, NullLogger.Instance);
        missing.Start();
        Assert.True(missing.Failed);
        Assert.False(missing.IsRunning);

        var engine = new Engine("missing", BackendOptions.LlamaCpp, new FakeEngine("x", [CapabilityKinds.Chat]), autostart: true, process: missing);
        var backend = Multi(null, engine);
        await backend.StartAsync(CancellationToken.None);
        Assert.Equal(NodeEngineInfo.Failed, backend.State("n").Engines.Single().State);
        Assert.Contains("is not a file", backend.State("n").Engines.Single().LastError);
    }

    // ---- helpers -------------------------------------------------------------------------

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();

        settings["Coordinator:Url"] = "http://localhost:5080/";
        settings["Ollama:Endpoint"] = "http://localhost:11434/";

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddInferHubNode();

        return builder.Build();
    }

    private sealed class FakeEngine(string model, string[] kinds, string? extra = null) : IInferenceBackend, IClosedSetScorer
    {
        private readonly ConcurrentQueue<string> calls = new();

        public bool Down { get; set; }

        public Task? Gate { get; set; }

        public IReadOnlyList<string> Calls => calls.ToArray();

        public string Name => "fake";

        public string Endpoint => "http://fake";

        public IReadOnlyList<string> Kinds => kinds;

        public bool SupportsModelManagement => false;

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>?>(Down
                ? null
                : extra is null ? [new ModelInfo(model, null, null)] : [new ModelInfo(model, null, null), new ModelInfo(extra, null, null)]);

        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => Record("generate", requestJson);

        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => Record("chat", requestJson);

        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => Record("embed", requestJson);

        public async IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Record("stream", requestJson);
            yield return "{}";
        }

        public Task<ToolResult> ScoreAsync(ToolJob job, CancellationToken cancellationToken)
        {
            calls.Enqueue($"score:{job.Model}");
            return Task.FromResult(new ToolResult(job.JobId, true, "{}", null));
        }

        public IAsyncEnumerable<ModelPullProgress> PullAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task WarmAsync(string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        private async Task<string> Record(string kind, string requestJson)
        {
            calls.Enqueue($"{kind}:{MultiBackend.ModelOf(requestJson)}");

            if (Gate is { } gate)
            {
                await gate;
            }

            return "{}";
        }
    }
}
