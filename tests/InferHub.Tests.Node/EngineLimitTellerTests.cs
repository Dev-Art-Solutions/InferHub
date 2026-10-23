using System.Diagnostics;
using System.Text.Json.Nodes;
using InferHub.Node.Backends.Colibri;
using InferHub.Node.Backends.Strata;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Contracts;

namespace InferHub.Tests;

/// <summary>
/// Phase 111. What colibri and Strata are told of their limits when they start — pure translations of
/// <see cref="EngineLimits"/> into each engine's own switches, so a table of them is the test. That the
/// engines obey is measured on the real engines, in the release notes.
/// </summary>
public sealed class EngineLimitTellerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "inferhub-teller-" + Guid.NewGuid().ToString("N"));

    public EngineLimitTellerTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ProcessStartInfo Coli() => new("python") { ArgumentList = { "coli", "serve", "--model", "m" } };

    [Theory]
    [InlineData(null, null, 64, null)]
    [InlineData(4, null, 64, 4)]
    [InlineData(null, 25, 64, 16)]
    [InlineData(8, 25, 64, 8)]
    [InlineData(null, 1, 8, 1)]
    public void ThreadsAreTheCoresOrTheShareWhicheverIsSmaller(int? cores, int? cpu, int logical, int? expected) =>
        Assert.Equal(expected, EngineLimitTelling.Threads(new EngineLimits(CpuPercent: cpu, Cores: cores), logical));

    [Fact]
    public void ColibriIsToldNinetyPercentOfItsMemoryInWholeGigabytes()
    {
        var info = Coli();

        var lines = new ColibriLimitTeller(() => 32).Tell(new EngineLimits(MemoryMiB: 65536, Cores: 6), info);

        Assert.Equal(["coli", "serve", "--model", "m", "--ram", "57"], info.ArgumentList);
        Assert.Equal("6", info.Environment["OMP_NUM_THREADS"]);
        Assert.Equal(["OMP_NUM_THREADS=6", "--ram 57"], lines);
    }

    [Fact]
    public void ColibriWithNoGpuIsToldSoInBothBackendsAndNeverWithAFlagThatExitsOnACpuBuild()
    {
        var info = Coli();
        info.Environment["COLI_VK_DEV"] = "0";
        info.Environment["CUDA_EXPERT_GB"] = "12";

        new ColibriLimitTeller().Tell(new EngineLimits(VramMiB: 8192, Gpus: []), info);

        Assert.Equal("0", info.Environment["COLI_CUDA"]);
        Assert.Equal("0", info.Environment["COLI_VULKAN"]);
        Assert.False(info.Environment.ContainsKey("COLI_VK_DEV"));
        Assert.False(info.Environment.ContainsKey("CUDA_EXPERT_GB"));
        Assert.False(info.Environment.ContainsKey("COLI_VK_DEVICE_CAP_MB"));
        Assert.DoesNotContain("--gpu", info.ArgumentList);
        Assert.DoesNotContain("--vram", info.ArgumentList);
    }

    [Fact]
    public void ColibriIsToldItsCardAndItsVramThroughEveryBudgetTheEngineReads()
    {
        var info = Coli();
        info.Environment["COLI_VK_DEV2"] = "auto";

        new ColibriLimitTeller().Tell(new EngineLimits(VramMiB: 12000, Gpus: [1]), info);

        Assert.Equal("1", info.Environment["COLI_VK_DEV"]);
        Assert.False(info.Environment.ContainsKey("COLI_VK_DEV2"));
        Assert.Equal("12000", info.Environment["COLI_VK_DEVICE_CAP_MB"]);
        Assert.Equal("9.7", info.Environment["CUDA_EXPERT_GB"]);
    }

    private string Config(object args, string? extra = null)
    {
        var path = Path.Combine(dir, "strata-coder-iq1_m.json");
        var node = new JsonObject
        {
            ["exe"] = "strata.exe",
            ["args"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args)),
            ["gpu"] = 0
        };

        if (extra is not null)
        {
            foreach (var (key, value) in JsonNode.Parse(extra)!.AsObject().ToList())
            {
                node[key] = value?.DeepClone();
            }
        }

        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    private static ProcessStartInfo Server(string config) =>
        new("python") { ArgumentList = { "serve/server.py", "--engine", "strata", "--config", config, "--port", "8095" } };

    private sealed class Cards(params long?[] totals) : IGpuCards
    {
        public long? TotalMiB(int index) => index < totals.Length ? totals[index] : null;
    }

    [Fact]
    public void StrataStartsOnACopyWhoseReserveLeavesItTheLimitAndTheOriginalIsUntouched()
    {
        var config = Config(new[] { "--expert-cache", "auto", "--vram-reserve-mib", "700" });
        var before = File.ReadAllText(config);
        var info = Server(config);

        var lines = new StrataLimitTeller(new Cards(24564)).Tell(new EngineLimits(VramMiB: 12000), info);

        var copy = info.ArgumentList[4];
        Assert.Equal(Path.Combine(dir, ".inferhub-limits-strata-coder-iq1_m.json"), copy);
        Assert.Equal(before, File.ReadAllText(config));
        var args = JsonNode.Parse(File.ReadAllText(copy))!["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        // 24564 MiB card, 12000 allowed: 12564 more kept free on top of the config's own 700.
        Assert.Equal(["--expert-cache", "auto", "--vram-reserve-mib", "13264"], args);
        Assert.Contains("--vram-reserve-mib 13264 (card 24564 MiB, 12000 MiB allowed)", lines);
    }

    [Fact]
    public void StrataWithNoReserveOfItsOwnStartsFromTheEnginesLargerDefault()
    {
        var info = Server(Config(new[] { "--expert-cache", "auto" }, """{"gpu": [1]}""")) ;

        new StrataLimitTeller(new Cards(null, 16384)).Tell(new EngineLimits(VramMiB: 8192), info);

        var args = JsonNode.Parse(File.ReadAllText(info.ArgumentList[4]))!["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal(["--expert-cache", "auto", "--vram-reserve-mib", (StrataLimitTeller.DefaultReserveMiB + 8192).ToString()], args);
    }

    [Fact]
    public void ACardWhoseSizeCannotBeReadIsSaidAndLeftToTheWatchdog()
    {
        var config = Config(new[] { "--expert-cache", "auto" });
        var info = Server(config);

        var lines = new StrataLimitTeller(new Cards()).Tell(new EngineLimits(VramMiB: 8192), info);

        Assert.Equal(config, info.ArgumentList[4]);
        Assert.Contains(lines, line => line.Contains("held by the watchdog only"));
    }

    [Fact]
    public void StratasPoolIsToldTheProcessorsAfterTheHostThreadsAndARamBudgetIsLowered()
    {
        var info = Server(Config(new[] { "--resident-budget-gib", "40" }));

        var lines = new StrataLimitTeller(new Cards(), () => 32).Tell(new EngineLimits(Cores: 6, MemoryMiB: 32768), info);

        var copy = JsonNode.Parse(File.ReadAllText(info.ArgumentList[4]))!;
        Assert.Equal("1,2,3,4,5", copy["pool_cores"]!.GetValue<string>());
        Assert.Equal(["--resident-budget-gib", "24"], copy["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["pool_cores 1-5", "--resident-budget-gib 24 (was 40)", "config .inferhub-limits-strata-coder-iq1_m.json"], lines);
    }

    [Fact]
    public void NothingToTellStartsTheOriginal()
    {
        var config = Config(new[] { "--expert-cache", "auto" });
        var info = Server(config);

        var lines = new StrataLimitTeller(new Cards(24564)).Tell(new EngineLimits(MemoryMiB: 32768), info);

        Assert.Empty(lines);
        Assert.Equal(config, info.ArgumentList[4]);
        Assert.False(File.Exists(Path.Combine(dir, ".inferhub-limits-strata-coder-iq1_m.json")));
    }
}
