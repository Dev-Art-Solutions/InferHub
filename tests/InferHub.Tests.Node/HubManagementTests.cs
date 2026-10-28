using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Fit;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 120 on the node: the coordinator's catalogue, estimate and install are answered by the node's own services,
/// refused while its owner has not allowed them, and an install is held to the owner's disk terms (D1–D3).
/// </summary>
public sealed class HubManagementTests : IAsyncLifetime
{
    private const string Repo = "someone/Moe-GGUF";

    private static readonly byte[] Q4 = GgufWriter.Llama(arch: "qwen3moe", layers: 8, heads: 16, kvHeads: 4, keyLength: 128, context: 32768, experts: 64, denseBytes: 2048, expertBytes: 8192);

    private FakeHuggingFace hub = null!;
    private readonly NodeOptions options = new();
    private int asked;

    public async Task InitializeAsync()
    {
        hub = await FakeHuggingFace.StartAsync();
        hub.Repo(Repo, ("README.md", "# moe"u8.ToArray()), ("Moe-Q4_K_M.gguf", Q4));
        hub.Info[Repo] = new { id = Repo, pipeline_tag = "text-generation", gguf = new { total = 30_000_000_000L, context_length = 32768 } };
    }

    public async Task DisposeAsync() => await hub.DisposeAsync();

    /// <summary>llama.cpp knows its disk (1 TiB free); this Ollama does not say where its models land.</summary>
    private static MachineFacts Machine(long llamaFreeMiB = 1 << 20) =>
        new([new GpuFacts(0, "card", 24576, 24000)], 65536, 60000, 16,
        [
            new EngineFacts(FitEngines.Ollama, "ollama", true, null, null, null, null, null),
            new EngineFacts(FitEngines.LlamaCpp, "gguf", true, null, "/m", llamaFreeMiB, null, null),
            new EngineFacts(FitEngines.Colibri, null, false, "this node has no colibri engine", null, null, null, null),
            new EngineFacts(FitEngines.Strata, null, false, "this node has no Strata install", null, null, null, null)
        ], []);

    private HubManagement Management(long llamaFreeMiB = 1 << 20)
    {
        var fit = new FitService(new ModelFactsReader(() => { asked++; return hub.Client(); }, new HttpClient(), new Uri("http://127.0.0.1:9/")), () => Machine(llamaFreeMiB));
        var catalog = new CatalogService(null, new HttpClient(), new Uri("http://127.0.0.1:9/"), fit.Machine);
        return new HubManagement(fit, catalog, TestOptions.Monitor(options), NullLogger<HubManagement>.Instance);
    }

    private static ToolJob Job(string operation, ManagementRequest request) =>
        new(Guid.NewGuid(), ManagementJob.Capability, operation, JsonSerializer.Serialize(request));

    [Fact]
    public async Task OffByDefaultEveryOperationIsRefusedWithTheReasonAndNothingIsAsked()
    {
        var management = Management();

        foreach (var operation in ManagementJob.Operations)
        {
            var result = await management.RunAsync(Job(operation, new ManagementRequest(Repo, 8192, FitEngines.LlamaCpp)), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(ManagementJob.NotAllowed, result.ErrorCode);
            Assert.Equal(403, ManagementJob.StatusOf(result.ErrorCode));
            Assert.Contains("Node:Management:AllowFromHub", result.Error);
        }

        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task TheOwnersAnswerIsReadLiveNotAtStartup()
    {
        var management = Management();
        var job = Job(ManagementJob.Estimate, new ManagementRequest(Repo, 8192));

        Assert.False((await management.RunAsync(job, CancellationToken.None)).Success);

        options.Management.AllowFromHub = true;

        Assert.True((await management.RunAsync(job, CancellationToken.None)).Success);
        Assert.True(management.Terms().AllowFromHub);
    }

    [Fact]
    public async Task TheEstimateIsTheNodesOwnWithTheTermsAndAVerdictPerOption()
    {
        options.Management.AllowFromHub = true;
        var management = Management();

        var result = await management.RunAsync(Job(ManagementJob.Estimate, new ManagementRequest(Repo, 8192)), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        using var json = JsonDocument.Parse(result.Payload!);
        var root = json.RootElement;
        Assert.Equal(8192, root.GetProperty("context").GetInt32());
        Assert.True(root.GetProperty("terms").GetProperty("allowFromHub").GetBoolean());
        Assert.Equal(20, root.GetProperty("terms").GetProperty("keepFreeGiB").GetInt32());

        var verdicts = root.GetProperty("hub").EnumerateArray().ToDictionary(v => v.GetProperty("engine").GetString()!, v => v);
        Assert.True(verdicts[FitEngines.LlamaCpp].GetProperty("allowed").GetBoolean());
        // An Ollama that does not say where its models land cannot be held to "keep 20 GiB free", so it is not allowed.
        Assert.False(verdicts[FitEngines.Ollama].GetProperty("allowed").GetBoolean());
        Assert.Contains("KeepFreeGiB", verdicts[FitEngines.Ollama].GetProperty("why").GetString());
        Assert.Equal(root.GetProperty("options").GetArrayLength(), verdicts.Count);
    }

    [Fact]
    public async Task AnInstallTheTermsAllowIsThePullWithTheChosenContext()
    {
        options.Management.AllowFromHub = true;
        var management = Management();

        var result = await management.RunAsync(Job(ManagementJob.Install, new ManagementRequest(Repo, 16384, FitEngines.LlamaCpp, "Q4_K_M")), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var install = JsonSerializer.Deserialize<ManagementInstall>(result.Payload!)!;
        Assert.Equal(FitEngines.LlamaCpp, install.Engine);
        Assert.Equal("Q4_K_M", install.Variant);
        Assert.Equal(ModelCommand.KindPull, install.Command.Kind);
        Assert.Equal(ModelCommand.EngineHuggingFace, install.Command.Engine);
        Assert.Equal(16384, install.Command.Context);
    }

    [Fact]
    public async Task AnInstallThatWouldEatTheFloorIsRefusedAsOverQuotaWithTheNumbers()
    {
        options.Management.AllowFromHub = true;
        options.Management.KeepFreeGiB = 20;
        var management = Management(llamaFreeMiB: 20 * 1024);

        var result = await management.RunAsync(Job(ManagementJob.Install, new ManagementRequest(Repo, 8192, FitEngines.LlamaCpp, "Q4_K_M")), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ManagementJob.OverQuota, result.ErrorCode);
        Assert.Equal(409, ManagementJob.StatusOf(result.ErrorCode));
        Assert.Contains("keeps 20 GiB free", result.Error);
    }

    [Fact]
    public async Task TheEstimatesOwnRefusalsKeepTheirStatus()
    {
        options.Management.AllowFromHub = true;
        var management = Management();

        var noEngine = await management.RunAsync(Job(ManagementJob.Install, new ManagementRequest(Repo, 8192)), CancellationToken.None);
        var badLink = await management.RunAsync(Job(ManagementJob.Estimate, new ManagementRequest("not a link at all !!", 8192)), CancellationToken.None);
        var unknown = await management.RunAsync(Job("delete", new ManagementRequest(Repo)), CancellationToken.None);

        Assert.Equal(400, ManagementJob.StatusOf(noEngine.ErrorCode));
        Assert.Equal(400, ManagementJob.StatusOf(badLink.ErrorCode));
        Assert.Equal(400, ManagementJob.StatusOf(unknown.ErrorCode));
        Assert.Contains("catalog, estimate, install", unknown.Error);
    }

    // ---- D3: the terms, pure ---------------------------------------------------------------------------------------

    private static FitOption Option(string engine, long diskMiB) =>
        new(engine, engine, "Q4_K_M", null, true, true, FitPlacements.Gpu, diskMiB, 0, diskMiB, 0, diskMiB, 8192, null, null, "fast", true, [], 50);

    [Theory]
    [InlineData(null, 20, 100_000L, 1_000_000L, true)]   // 98 GiB, 976 GiB free: fine
    [InlineData(50, 20, 100_000L, 1_000_000L, false)]    // over the per-model cap
    [InlineData(null, 20, 100_000L, 110_000L, false)]    // would leave under 20 GiB
    [InlineData(null, 0, 100_000L, 100_000L, true)]      // no floor: the estimate's own disk check is the only one
    [InlineData(100, 0, 102_400L, 1_000_000L, true)]     // exactly the cap
    public void TheTermsAreACapPerModelAndAFloorOfFreeDisk(int? maxModel, int keepFree, long diskMiB, long freeMiB, bool allowed)
    {
        var terms = new ManagementOptions { AllowFromHub = true, MaxModelGiB = maxModel, KeepFreeGiB = keepFree };

        var refusal = ManagementQuota.Refusal(terms, Option(FitEngines.LlamaCpp, diskMiB), Machine(freeMiB));

        Assert.Equal(allowed, refusal is null);
    }

    [Fact]
    public void ADiskTheNodeCannotMeasureIsRefusedOnlyWhileTheOwnerAskedForAFloor()
    {
        var floor = new ManagementOptions { AllowFromHub = true, KeepFreeGiB = 20 };
        var none = new ManagementOptions { AllowFromHub = true, KeepFreeGiB = 0 };

        Assert.Contains("cannot tell", ManagementQuota.Refusal(floor, Option(FitEngines.Ollama, 5000), Machine()));
        Assert.Null(ManagementQuota.Refusal(none, Option(FitEngines.Ollama, 5000), Machine()));
    }
}
