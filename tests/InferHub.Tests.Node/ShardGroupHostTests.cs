using InferHub.Node.Backends;
using InferHub.Node.Backends.Shards;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 136 on the head: what <c>llama-server</c> is told (every layer on a worker, the shares by offer, loopback, the group's
/// name — D3); a model is a name, never a path (D2); a group waits for every member's door, runs, answers under its name and
/// stops when the hub's list drops it (D1); requests for the group's name go to it and everything else is untouched (D4).
/// </summary>
public sealed class ShardGroupHostTests : IDisposable
{
    private readonly string models = Path.Combine(Path.GetTempPath(), "inferhub-groups-" + Guid.NewGuid().ToString("N"));

    public ShardGroupHostTests()
    {
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "llama31-8b.gguf"), "not really a model");
    }

    private static ShardGroupSpec Spec(int context = 4096) => new("llama-8b-split", "llama31-8b.gguf", context, 3072,
        [new ShardGroupWorker("b", "shard-b", 3072), new ShardGroupWorker("c", "shard-c", 6144)]);

    private static ShardPeerState Door(string id, string local) => new(id, "shard-" + id, id + ":50053", local, 0.3, null, DateTimeOffset.UtcNow);

    [Fact]
    public void TheHeadRunsEveryLayerOnAWorkerInProportionToTheOffersOnLoopbackUnderTheGroupsName()
    {
        var args = ShardGroupHost.Arguments(Spec(), "/models/llama31-8b.gguf", ["127.0.0.1:50052", "127.0.0.1:41001", "127.0.0.1:41002"], 18090, 4);

        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];

        Assert.Equal("/models/llama31-8b.gguf", After("-m"));
        Assert.Equal("127.0.0.1:50052,127.0.0.1:41001,127.0.0.1:41002", After("--rpc"));
        Assert.Equal("RPC0,RPC1,RPC2", After("--device"));
        Assert.Equal("999", After("-ngl"));
        Assert.Equal("3072,3072,6144", After("--tensor-split"));
        Assert.Equal("127.0.0.1", After("--host"));
        Assert.Equal("18090", After("--port"));
        Assert.Equal("llama-8b-split", After("--alias"));
        Assert.Equal("4", After("-t"));
        Assert.Equal("4096", After("-c"));
    }

    [Theory]
    [InlineData("../etc/passwd", "not a file name")]
    [InlineData("/models/x.gguf", "not a file name")]
    [InlineData("sub\\x.gguf", "not a file name")]
    [InlineData("missing.gguf", "is not in")]
    public void AModelIsANameInTheModelsDirectoryNeverAPath(string model, string problem) =>
        Assert.Contains(problem, ShardGroupHost.ModelFile(models, model).Problem);

    [Fact]
    public void AModelThatIsThereIsItsPathAndNoDirectoryIsSaid()
    {
        Assert.Equal(Path.Combine(models, "llama31-8b.gguf"), ShardGroupHost.ModelFile(models, "llama31-8b.gguf").Path);
        Assert.Contains("Node:Shards:ModelsDir", ShardGroupHost.ModelFile(null, "llama31-8b.gguf").Problem);
    }

    [Fact]
    public async Task AGroupWaitsForEveryMembersDoorThenRunsAnswersUnderItsNameAndStopsWhenTheHubDropsIt()
    {
        var doors = new List<ShardPeerState> { Door("b", "127.0.0.1:41001") };
        var healthy = true;
        await using var host = new ShardGroupHost(
            new ShardOptions { Enabled = true, RamGiB = 3, AllowHead = true },
            models,
            () => "dotnet",  // a real executable: the supervisor runs it; health is faked below
            () => doors,
            (_, port) => new NamedBackend($"group@{port}"),
            _ => Task.FromResult(healthy),
            NullLogger.Instance,
            null);
        await host.StartAsync(CancellationToken.None);

        host.SetGroups(new ShardGroupList([Spec()]));
        var waiting = Assert.Single(host.State());
        Assert.Equal(ShardGroupState.Waiting, waiting.State);
        Assert.Contains("no tunnel to shard-c yet", waiting.Error);
        Assert.Null(host.BackendFor("llama-8b-split"));

        // shard-c's door opens (135): the head starts, and once /health answers the group is a model here.
        doors.Add(Door("c", "127.0.0.1:41002"));
        host.SetGroups(new ShardGroupList([Spec()]));

        for (var i = 0; i < 50 && host.BackendFor("llama-8b-split") is null; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(ShardGroupState.Running, host.State().Single().State);
        Assert.Equal(["llama-8b-split"], host.Models());
        Assert.StartsWith("group@", host.BackendFor("LLAMA-8B-SPLIT")!.Name);

        // The hub stops listing it: out of the routes at once, and — with nothing in flight — gone after its drain (137).
        host.SetGroups(new ShardGroupList([]));
        Assert.Null(host.Acquire("llama-8b-split"));

        for (var i = 0; i < 40 && host.State().Count > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Empty(host.State());
        Assert.Empty(host.Models());
        Assert.Null(host.BackendFor("llama-8b-split"));
    }

    [Fact]
    public void WithoutTheOwnersConsentNothingRunsWhateverTheHubSends()
    {
        var host = new ShardGroupHost(new ShardOptions { Enabled = true, RamGiB = 3 }, models, () => "dotnet",
            () => [Door("b", "127.0.0.1:41001"), Door("c", "127.0.0.1:41002")], (_, _) => new NamedBackend("x"), _ => Task.FromResult(true), NullLogger.Instance, null);

        Assert.False(host.Enabled);
        host.SetGroups(new ShardGroupList([Spec()]));
        Assert.Empty(host.State());
    }

    [Fact]
    public async Task OnAHeadTheGroupsNameGoesToTheGroupAndEverythingElseToTheNodesOwnBackend()
    {
        var own = new NamedBackend("ollama", [new ModelInfo("qwen2.5:0.5b", "d", 1)]);
        await using var host = new ShardGroupHost(new ShardOptions { Enabled = true, RamGiB = 3, AllowHead = true }, models, () => "dotnet",
            () => [Door("b", "127.0.0.1:41001"), Door("c", "127.0.0.1:41002")], (_, _) => new NamedBackend("group"), _ => Task.FromResult(true), NullLogger.Instance, null);
        await host.StartAsync(CancellationToken.None);
        host.SetGroups(new ShardGroupList([Spec()]));

        for (var i = 0; i < 50 && host.BackendFor("llama-8b-split") is null; i++)
        {
            await Task.Delay(100);
        }

        var backend = new ShardGroupBackend(own, host);

        Assert.Equal("group", await backend.ChatAsync("""{"model":"llama-8b-split","messages":[]}""", CancellationToken.None));
        Assert.Equal("ollama", await backend.ChatAsync("""{"model":"qwen2.5:0.5b","messages":[]}""", CancellationToken.None));
        Assert.Equal("ollama", await backend.ChatAsync("not json", CancellationToken.None));
        Assert.Equal(["qwen2.5:0.5b", "llama-8b-split"], (await backend.ListModelsAsync(CancellationToken.None))!.Select(m => m.Name));
        Assert.Equal([CapabilityKinds.Chat], backend.KindsFor("llama-8b-split"));
    }

    [Fact]
    public async Task AHeadWhoseOwnEngineCannotListStillSaysWhichGroupsItRuns()
    {
        // Found in the lab: shard-a's own Ollama was down (it lists null, so the node leaves the hub's inventory alone),
        // and the group it stopped stayed in the catalogue.
        var deadEngine = new NamedBackend("ollama", listsNothing: true);
        await using var host = new ShardGroupHost(new ShardOptions { Enabled = true, RamGiB = 3, AllowHead = true }, models, () => "dotnet",
            () => [Door("b", "127.0.0.1:41001"), Door("c", "127.0.0.1:41002")], (_, _) => new NamedBackend("group"), _ => Task.FromResult(true), NullLogger.Instance, null);
        await host.StartAsync(CancellationToken.None);
        var backend = new ShardGroupBackend(deadEngine, host);

        Assert.Null(await backend.ListModelsAsync(CancellationToken.None));

        host.SetGroups(new ShardGroupList([Spec()]));

        for (var i = 0; i < 50 && host.BackendFor("llama-8b-split") is null; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(["llama-8b-split"], (await backend.ListModelsAsync(CancellationToken.None))!.Select(m => m.Name));

        host.SetGroups(new ShardGroupList([]));
        Assert.Empty((await backend.ListModelsAsync(CancellationToken.None))!);
    }

    private async Task<ShardGroupHost> RunningHostAsync(Func<IReadOnlyList<ShardPeerState>>? doors = null)
    {
        var host = new ShardGroupHost(new ShardOptions { Enabled = true, RamGiB = 3, AllowHead = true }, models, () => "dotnet",
            doors ?? (() => [Door("b", "127.0.0.1:41001"), Door("c", "127.0.0.1:41002")]), (_, port) => new NamedBackend($"group@{port}"),
            _ => Task.FromResult(true), NullLogger.Instance, null) { DrainFor = TimeSpan.FromSeconds(10) };
        await host.StartAsync(CancellationToken.None);
        host.SetGroups(new ShardGroupList([Spec()]));

        for (var i = 0; i < 50 && host.BackendFor("llama-8b-split") is null; i++)
        {
            await Task.Delay(100);
        }

        Assert.NotNull(host.BackendFor("llama-8b-split"));
        return host;
    }

    [Fact]
    public async Task AStoppingGroupFinishesTheRequestInFlightTakesNoNewOneAndThenStops()
    {
        await using var host = await RunningHostAsync();
        var inFlight = host.Acquire("llama-8b-split")!;

        // The hub takes the group back (a member's owner paused it).
        host.SetGroups(new ShardGroupList([]));

        var draining = Assert.Single(host.State());
        Assert.Equal(ShardGroupState.Draining, draining.State);
        Assert.Contains("finishing 1 request", draining.Error);
        Assert.Null(host.Acquire("llama-8b-split"));
        Assert.Contains("draining", host.Unavailable("llama-8b-split"));

        await Task.Delay(700);
        Assert.Equal(ShardGroupState.Draining, Assert.Single(host.State()).State);

        inFlight.Dispose();

        for (var i = 0; i < 40 && host.State().Count > 0; i++)
        {
            await Task.Delay(100);
        }

        Assert.Empty(host.State());
        Assert.Null(host.Unavailable("llama-8b-split"));
    }

    [Fact]
    public async Task AReassembledGroupStartsOnlyOnceTheOldOneHasStopped()
    {
        await using var host = await RunningHostAsync(() => [Door("b", "127.0.0.1:41001"), Door("c", "127.0.0.1:41002"), Door("d", "127.0.0.1:41003")]);
        var inFlight = host.Acquire("llama-8b-split")!;

        // shard-d stands in for shard-c: the same name, other members.
        var reassembled = Spec() with { Members = [new ShardGroupWorker("b", "shard-b", 3072), new ShardGroupWorker("d", "shard-d", 3072)] };
        host.SetGroups(new ShardGroupList([reassembled]));

        Assert.Equal([ShardGroupState.Draining], host.State().Select(s => s.State));

        inFlight.Dispose();

        for (var i = 0; i < 50 && host.State().FirstOrDefault()?.Members.Contains("shard-d") != true; i++)
        {
            await Task.Delay(100);
        }

        var now = Assert.Single(host.State());
        Assert.Equal(["shard-b", "shard-d"], now.Members);
    }

    [Fact]
    public async Task TheHeadSaysWhichWorkerHoldsWhichLayersByTheModelsOwnBlockCount()
    {
        File.WriteAllBytes(Path.Combine(models, "llama31-8b.gguf"), GgufWriter.Llama(layers: 32));
        var host = new ShardGroupHost(new ShardOptions { Enabled = true, RamGiB = 3, AllowHead = true }, models, () => "dotnet",
            () => [], (_, _) => new NamedBackend("x"), _ => Task.FromResult(true), NullLogger.Instance, null) { HeadName = "shard-a" };

        var layers = await host.LayersAsync(Spec(), Path.Combine(models, "llama31-8b.gguf"));

        Assert.Equal(
            [new ShardGroupLayers("shard-a", 0, 9, false), new ShardGroupLayers("shard-b", 9, 8, false), new ShardGroupLayers("shard-c", 17, 15, true)],
            layers);
        Assert.Null(await host.LayersAsync(Spec(), Path.Combine(models, "missing.gguf")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(models, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Answers every call with its own name, so a test can see which backend a request reached.</summary>
    private sealed class NamedBackend(string name, IReadOnlyList<ModelInfo>? models = null, bool listsNothing = false) : IInferenceBackend
    {
        public string Name => name;
        public string Endpoint => "test";
        public IReadOnlyList<string> Kinds => [CapabilityKinds.Chat];
        public bool SupportsModelManagement => false;
        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult(listsNothing ? null : models ?? (IReadOnlyList<ModelInfo>?)[]);
        public Task<string> GenerateAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult(name);
        public Task<string> ChatAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult(name);
        public Task<string> EmbedAsync(string requestJson, CancellationToken cancellationToken) => Task.FromResult(name);

        public async IAsyncEnumerable<string> StreamAsync(string kind, string requestJson, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return name;
        }

        public async IAsyncEnumerable<ModelPullProgress> PullAsync(string model, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public Task DeleteAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WarmAsync(string model, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
