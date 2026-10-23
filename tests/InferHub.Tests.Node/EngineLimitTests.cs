using System.Diagnostics;
using InferHub.Node.Configuration;
using InferHub.Node.Profiles;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 102. The node's runtime limits are its ceiling and a coordinator may only go lower (43 D1): the
/// arithmetic and the clamp are pure, so they are a table here; the limiter's decisions — what is in
/// force, how each is held, when a runtime is stopped — run against a scripted container and scripted
/// readings. The real Job Object is in the Mesh suite, which is where a process is started.
/// </summary>
public class EngineLimitTests
{
    private static LocalCeiling Ceiling(IReadOnlyDictionary<string, EngineLimits>? limits) =>
        new(Array.Empty<string>(), false, Array.Empty<string>(), null, false, EngineLimitCeiling: limits);

    private static NodeProfile Profile(IReadOnlyDictionary<string, EngineLimits> limits) =>
        new("p", 1, new NodeProfileSelector(NodeId: "n"), Limits: limits);

    [Fact]
    public void AHubValueAtOrBelowTheNodesIsKept()
    {
        var local = Ceiling(new Dictionary<string, EngineLimits> { ["llamacpp"] = new(CpuPercent: 80, MemoryMiB: 16384) });

        var result = NodeProfileClamp.Apply(local, Profile(new Dictionary<string, EngineLimits> { ["llamacpp"] = new(CpuPercent: 50, MemoryMiB: 16384) }));

        Assert.Empty(result.Refusals);
        Assert.Equal(new EngineLimits(CpuPercent: 50, MemoryMiB: 16384), result.Effective.Limits!["llamacpp"]);
    }

    [Fact]
    public void AHubValueAboveTheNodesIsRefusedByNameAndTheNodesStays()
    {
        var local = Ceiling(new Dictionary<string, EngineLimits> { ["strata"] = new(VramMiB: 12000, Cores: 8) });

        var result = NodeProfileClamp.Apply(local, Profile(new Dictionary<string, EngineLimits> { ["strata"] = new(VramMiB: 20000, Cores: 4) }));

        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("limits:strata:vramMiB", refusal.Item);
        Assert.Contains("Node:ResourceLimits:Engines:strata:VramMiB is 12000 MiB", refusal.Reason);
        Assert.Contains("20000", refusal.Reason);

        // The rest of the block still applies (43 D6): per field, never all or nothing.
        Assert.Equal(new EngineLimits(Cores: 4), result.Effective.Limits!["strata"]);
        Assert.Equal(new EngineLimits(Cores: 4, VramMiB: 12000), EngineLimitMath.Effective(local.EngineLimits["strata"], result.Effective.Limits!["strata"]));
    }

    [Fact]
    public void ARuntimeTheNodeDidNotLimitMayBeLimitedBecauseThatIsNarrowing()
    {
        var result = NodeProfileClamp.Apply(Ceiling(null), Profile(new Dictionary<string, EngineLimits> { ["ollama"] = new(MemoryMiB: 4096) }));

        Assert.Empty(result.Refusals);
        Assert.Equal(4096, result.Effective.Limits!["ollama"].MemoryMiB);
    }

    [Fact]
    public void AGpuOutsideTheNodesListRefusesTheWholeList()
    {
        var local = Ceiling(new Dictionary<string, EngineLimits> { ["colibri"] = new(Gpus: [0]) });

        var result = NodeProfileClamp.Apply(local, Profile(new Dictionary<string, EngineLimits> { ["colibri"] = new(Gpus: [0, 1]) }));

        Assert.Contains("never add GPU 1", Assert.Single(result.Refusals).Reason);
        Assert.False(result.Effective.Limits!.ContainsKey("colibri"));
    }

    [Theory]
    [InlineData("llama.cpp", "llamacpp")]
    [InlineData("OLLAMA", "ollama")]
    public void RuntimeNamesAreCanonical(string asked, string stored)
    {
        var result = NodeProfileClamp.Apply(Ceiling(null), Profile(new Dictionary<string, EngineLimits> { [asked] = new(Cores: 2) }));

        Assert.True(result.Effective.Limits!.ContainsKey(stored));
    }

    [Theory]
    [InlineData("vllm")]
    [InlineData("/bin/sh")]
    [InlineData("tools; rm -rf /")]
    public void ANameThatIsNoRuntimeIsRefused(string name)
    {
        var result = NodeProfileClamp.Apply(Ceiling(null), Profile(new Dictionary<string, EngineLimits> { [name] = new(Cores: 2) }));

        Assert.Contains("names no runtime", Assert.Single(result.Refusals).Reason);
        Assert.Empty(result.Effective.Limits!);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(null, 0)]
    [InlineData(null, -5)]
    public void AValueNoLimitCanBeIsRefused(int? cpu, int? memory)
    {
        var result = NodeProfileClamp.Apply(Ceiling(null), Profile(new Dictionary<string, EngineLimits> { ["tools"] = new(CpuPercent: cpu, MemoryMiB: memory) }));

        Assert.Contains("not a usable limit", Assert.Single(result.Refusals).Reason);
    }

    [Fact]
    public void NoLimitsBlockLeavesOnlyTheNodesOwn()
    {
        var result = NodeProfileClamp.Apply(Ceiling(null), new NodeProfile("p", 1, new NodeProfileSelector(NodeId: "n")));

        Assert.Null(result.Effective.Limits);
    }

    [Fact]
    public void EffectiveIsTheSmallerPerFieldAndTheIntersectionOfGpus()
    {
        var effective = EngineLimitMath.Effective(
            new EngineLimits(CpuPercent: 80, MemoryMiB: 8192, Gpus: [0, 1]),
            new EngineLimits(CpuPercent: 50, VramMiB: 6000, Gpus: [1]));

        Assert.Equal(50, effective.CpuPercent);
        Assert.Equal(8192, effective.MemoryMiB);
        Assert.Equal(6000, effective.VramMiB);
        Assert.Equal([1], effective.Gpus);
    }

    [Theory]
    [InlineData("0,1", new[] { 0, 1 })]
    [InlineData(" 1 , 0 ,1", new[] { 0, 1 })]
    [InlineData("none", new int[0])]
    public void GpusParseAsCudaVisibleDevicesDoes(string text, int[] expected)
    {
        Assert.True(EngineLimitOptions.TryParseGpus(text, out var devices));
        Assert.Equal(expected, devices);
    }

    [Theory]
    [InlineData("0;1")]
    [InlineData("gpu0")]
    [InlineData("-1")]
    public void GpusThatAreNotIndexesDoNotParse(string text) =>
        Assert.False(EngineLimitOptions.TryParseGpus(text, out _));

    [Fact]
    public void TheValidatorRefusesARuntimeNobodyRunsAndABadValue()
    {
        var options = new NodeOptions();
        options.ResourceLimits.Engines["llama"] = new EngineLimitOptions { Cores = 2 };
        options.ResourceLimits.Engines["strata"] = new EngineLimitOptions { CpuPercent = 150, Gpus = "0;1" };

        var result = new NodeOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Node:ResourceLimits:Engines:llama names no runtime"));
        Assert.Contains(result.Failures!, f => f.Contains("Engines:strata:CpuPercent must be between 1 and 100"));
        Assert.Contains(result.Failures!, f => f.Contains("Engines:strata:Gpus"));
    }

    // ---------------------------------------------------------------- the limiter

    [Fact]
    public void TheLimitInForceIsTheSmallerOfTheNodesAndTheHubs()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["llamacpp"] = new() { MemoryMiB = 8192, CpuPercent = 80 } });
        rig.Limiter.Track("llamacpp", 1, null, "*");

        rig.Limiter.SetHubLimits(new Dictionary<string, EngineLimits> { ["llamacpp"] = new(CpuPercent: 40) });

        var row = Assert.Single(rig.Limiter.State("n").Groups);
        Assert.Equal(new EngineLimits(CpuPercent: 80, MemoryMiB: 8192), row.Node);
        Assert.Equal(new EngineLimits(CpuPercent: 40), row.Hub);
        Assert.Equal(new EngineLimits(CpuPercent: 40, MemoryMiB: 8192), row.Effective);
        Assert.Equal(new EngineLimits(CpuPercent: 40, MemoryMiB: 8192), rig.Factory.Created["llamacpp"].Applied);
    }

    [Fact]
    public void ANodeCeilingLoweredLaterStillWinsOverAnEarlierHubValue()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["tools"] = new() { Cores = 8 } });
        rig.Limiter.Track("tools", 1, null, "*");
        rig.Limiter.SetHubLimits(new Dictionary<string, EngineLimits> { ["tools"] = new(Cores: 6) });

        rig.Options.CurrentValue.ResourceLimits.Engines["tools"].Cores = 2;
        rig.Limiter.Poll();

        Assert.Equal(2, rig.Limiter.Effective("tools").Cores);
        Assert.Equal(2, rig.Factory.Created["tools"].Applied!.Cores);
    }

    [Fact]
    public void AProcessStartedBeforeAnyLimitGoesUnderTheContainerWhenOneArrives()
    {
        using var rig = new Rig();
        rig.Limiter.Track("ollama", 4242, null, "*");

        Assert.False(rig.Factory.Created.ContainsKey("ollama"));

        rig.Limiter.SetHubLimits(new Dictionary<string, EngineLimits> { ["ollama"] = new(MemoryMiB: 2048) });

        Assert.Equal(2048, rig.Factory.Created["ollama"].Applied!.MemoryMiB);
        Assert.Equal(1, rig.Limiter.State("n").Groups.Single().Processes);
    }

    [Fact]
    public void MemoryTheOsDoesNotHoldIsWatchedAndTheRuntimeIsStoppedAboveIt()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["colibri"] = new() { MemoryMiB = 1000 } });
        rig.Limiter.Track("colibri", 10, null, "*");
        rig.Inspector.Children[10] = [11];
        rig.Inspector.Memory[10] = 600L * 1024 * 1024;
        rig.Inspector.Memory[11] = 500L * 1024 * 1024;

        rig.Limiter.Poll();

        Assert.Equal([10], rig.Killed);
        var row = rig.Limiter.State("n").Groups.Single();
        Assert.Equal(1, row.Breaches);
        Assert.Contains("1100 MiB of memory, over its limit of 1000 MiB", row.LastBreach);
        Assert.Equal("watchdog (stops the runtime above it)", row.Enforcement[LimitResources.Memory]);
        Assert.Equal(0, row.Processes);
    }

    [Fact]
    public void MemoryTheOsHoldsIsLeftToTheOs()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["colibri"] = new() { MemoryMiB = 1000 } }, holdsMemory: true);
        rig.Limiter.Track("colibri", 10, null, "*");
        rig.Inspector.Memory[10] = 2000L * 1024 * 1024;

        rig.Limiter.Poll();

        Assert.Empty(rig.Killed);
        Assert.Equal("os (fake)", rig.Limiter.State("n").Groups.Single().Enforcement[LimitResources.Memory]);
    }

    [Fact]
    public void GpuMemoryOverTheLimitStopsTheRuntimeChildrenCounted()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["ollama"] = new() { VramMiB = 4000 } });
        rig.Limiter.Track("ollama", 20, null, "*");
        rig.Inspector.Children[20] = [21];
        rig.Inspector.Gpu = new Dictionary<int, long> { [21] = 5000L * 1024 * 1024, [99] = 9000L * 1024 * 1024 };

        rig.Limiter.Poll();

        Assert.Equal([20], rig.Killed);
        Assert.Contains("5000 MiB of GPU memory", rig.Limiter.State("n").Groups.Single().LastBreach);
    }

    [Fact]
    public void GpuMemoryUnderTheLimitIsReportedAndNothingIsStopped()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["ollama"] = new() { VramMiB = 4000 } });
        rig.Limiter.Track("ollama", 20, null, "*");
        rig.Inspector.Gpu = new Dictionary<int, long> { [20] = 3000L * 1024 * 1024 };

        rig.Limiter.Poll();

        Assert.Empty(rig.Killed);
        Assert.Equal(3000, rig.Limiter.State("n").Groups.Single().VramMiB);
    }

    [Fact]
    public void AVramLimitOnABoxWithNoReadingSaysNothingHoldsIt()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["strata"] = new() { VramMiB = 4000 } }, canReadGpu: false);

        rig.Limiter.Track("strata", 5, null, "*");

        Assert.StartsWith("none:", rig.Limiter.State("n").Groups.Single().Enforcement[LimitResources.Vram]);
    }

    [Fact]
    public void AGpuListPutsTheDevicesInTheLaunchEnvironment()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["llamacpp"] = new() { Gpus = "1" } });
        var info = new ProcessStartInfo("x");

        rig.Limiter.Prepare("llamacpp", info);

        Assert.Equal("1", info.Environment["CUDA_VISIBLE_DEVICES"]);
    }

    [Fact]
    public void NoGpuAtAllIsMinusOne()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["ollama"] = new() { Gpus = "none" } });
        var info = new ProcessStartInfo("x");

        rig.Limiter.Prepare("ollama", info);

        Assert.Equal("-1", info.Environment["CUDA_VISIBLE_DEVICES"]);
    }

    [Fact]
    public void NoGpuLimitLeavesTheEnvironmentAlone()
    {
        using var rig = new Rig();
        var info = new ProcessStartInfo("x");
        info.Environment.Remove("CUDA_VISIBLE_DEVICES");

        rig.Limiter.Prepare("ollama", info);

        Assert.False(info.Environment.ContainsKey("CUDA_VISIBLE_DEVICES"));
    }

    [Fact]
    public void ARuntimeStartedSeeingOtherGpusIsRestarted()
    {
        using var rig = new Rig();
        rig.Limiter.Track("strata", 30, null, "*");

        rig.Limiter.SetHubLimits(new Dictionary<string, EngineLimits> { ["strata"] = new(Gpus: [0]) });

        Assert.Equal([30], rig.Killed);
        Assert.Equal(0, rig.Limiter.State("n").Groups.Single().Breaches);
    }

    [Fact]
    public void ARuntimeStartedWithTheGpusInForceIsLeftAlone()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["strata"] = new() { Gpus = "0" } });
        rig.Limiter.Track("strata", 30, null, "0");

        rig.Limiter.Poll();

        Assert.Empty(rig.Killed);
    }

    [Fact]
    public void ChangedFiresWhenALimitMovesAndNotOnAQuietPoll()
    {
        using var rig = new Rig();
        var fired = 0;
        rig.Limiter.Changed += () => fired++;

        rig.Limiter.Poll();
        var quiet = fired;
        rig.Limiter.SetHubLimits(new Dictionary<string, EngineLimits> { ["tools"] = new(Cores: 1) });
        var afterSet = fired;
        rig.Limiter.Poll();

        Assert.Equal(quiet + 1, afterSet);
        Assert.Equal(afterSet, fired);
    }

    [Fact]
    public void ALimitOnARuntimeNotRunningMakesNoOsObjectAndSaysWhenItApplies()
    {
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["colibri"] = new() { MemoryMiB = 1000 } });

        rig.Limiter.Poll();

        Assert.Empty(rig.Factory.Created);
        Assert.Equal("applies when the runtime starts", rig.Limiter.State("n").Groups.Single().Enforcement[LimitResources.Memory]);
    }

    [Fact]
    public void NothingLimitedAndNothingRunningReportsNoRows()
    {
        using var rig = new Rig();

        Assert.Empty(rig.Limiter.State("n").Groups);
    }

    [Fact]
    public void ARunningRuntimeWithNoLimitStillShowsUp()
    {
        using var rig = new Rig();
        rig.Limiter.Track("llamacpp", 7, null, "*");

        var row = Assert.Single(rig.Limiter.State("n").Groups);
        Assert.Equal("llamacpp", row.Group);
        Assert.True(row.Effective.IsEmpty);
        Assert.Equal(1, row.Processes);
    }

    [Fact]
    public void ARuntimeWithATellerIsToldItsLimitsAtLaunchAndTheReportSaysWhat()
    {
        // Phase 111: the limit is held from outside (102) AND said to the runtime in its own words.
        EngineLimiter.RegisterTeller("colibri", new InferHub.Node.Backends.Colibri.ColibriLimitTeller());
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["colibri"] = new() { MemoryMiB = 10240, Cores = 4 } });
        var info = new ProcessStartInfo("python") { ArgumentList = { "coli", "serve" } };

        rig.Limiter.Prepare("colibri", info);

        Assert.Equal(["coli", "serve", "--ram", "9"], info.ArgumentList);
        Assert.Equal("4", info.Environment["OMP_NUM_THREADS"]);
    }

    [Fact]
    public void ARuntimeToldOtherLimitsThanTheOnesInForceIsStartedAgain()
    {
        EngineLimiter.RegisterTeller("colibri", new InferHub.Node.Backends.Colibri.ColibriLimitTeller());
        using var rig = new Rig(new Dictionary<string, EngineLimitOptions> { ["colibri"] = new() { MemoryMiB = 8192 } });
        rig.Limiter.Track("colibri", 10, null, "*", new EngineLimiter.Telling("memory 8192 MiB", ["--ram 7"]));
        rig.Limiter.Track("colibri", 11, null, "*", new EngineLimiter.Telling("memory 16384 MiB", ["--ram 14"]));
        rig.Limiter.Track("colibri", 12, null, "*");

        rig.Limiter.Poll();

        // Told the old number: stopped for its supervisor to start under the new one. Told the right one,
        // or never told (a process a node before 111 adopted): left alone. Not a breach either way.
        Assert.Equal([11], rig.Killed);
        var row = rig.Limiter.State("n").Groups.Single();
        Assert.Equal(0, row.Breaches);
        Assert.Equal(["--ram 7"], row.Told);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Dictionary<string, EngineLimitOptions>? node = null, bool holdsMemory = false, bool canReadGpu = true)
        {
            var options = new NodeOptions();

            foreach (var (key, value) in node ?? new())
            {
                options.ResourceLimits.Engines[key] = value;
            }

            Options = new StaticMonitor(options);
            Factory = new FakeFactory(holdsMemory);
            Inspector = new FakeInspector { CanReadGpuMemory = canReadGpu };
            Limiter = new EngineLimiter(Options, TimeProvider.System, NullLogger<EngineLimiter>.Instance, Factory, Inspector, (_, pid) =>
            {
                Killed.Add(pid);
                Inspector.Memory.Remove(pid);
                Inspector.Gone.Add(pid);
            });
        }

        public StaticMonitor Options { get; }

        public FakeFactory Factory { get; }

        public FakeInspector Inspector { get; }

        public EngineLimiter Limiter { get; }

        public List<int> Killed { get; } = [];

        public void Dispose() => Limiter.Dispose();
    }

    private sealed class StaticMonitor(NodeOptions value) : IOptionsMonitor<NodeOptions>
    {
        public NodeOptions CurrentValue { get; } = value;

        public NodeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NodeOptions, string?> listener) => null;
    }

    private sealed class FakeFactory(bool holdsMemory) : IProcessContainerFactory
    {
        public Dictionary<string, FakeContainer> Created { get; } = new();

        public IProcessContainer Create(string group) => Created[group] = new FakeContainer(holdsMemory);
    }

    private sealed class FakeContainer(bool holdsMemory) : IProcessContainer
    {
        public EngineLimits? Applied { get; private set; }

        public void Add(Process process)
        {
        }

        public IReadOnlyDictionary<string, string> Apply(EngineLimits limits)
        {
            Applied = limits;
            return holdsMemory && limits.MemoryMiB is not null
                ? new Dictionary<string, string> { [LimitResources.Memory] = "os (fake)" }
                : new Dictionary<string, string>();
        }

        public IReadOnlyList<int>? ProcessIds() => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeInspector : IProcessInspector
    {
        public Dictionary<int, int[]> Children { get; } = new();

        public Dictionary<int, long> Memory { get; } = new();

        public HashSet<int> Gone { get; } = [];

        public IReadOnlyDictionary<int, long>? Gpu { get; set; } = new Dictionary<int, long>();

        public bool CanReadGpuMemory { get; init; } = true;

        public IReadOnlyList<int> WithDescendants(IReadOnlyCollection<int> roots)
        {
            var all = new List<int>();
            var queue = new Queue<int>(roots);

            while (queue.Count > 0)
            {
                var pid = queue.Dequeue();

                if (Gone.Contains(pid))
                {
                    continue;
                }

                all.Add(pid);

                foreach (var child in Children.GetValueOrDefault(pid) ?? [])
                {
                    queue.Enqueue(child);
                }
            }

            return all;
        }

        public ProcessSample? Sample(int pid) =>
            Gone.Contains(pid) ? null : new ProcessSample(TimeSpan.Zero, Memory.GetValueOrDefault(pid));

        public IReadOnlyDictionary<int, long>? GpuMemory() => CanReadGpuMemory ? Gpu : null;
    }
}
