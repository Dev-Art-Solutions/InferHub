using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Backends;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Strata;
using InferHub.Node.Fit;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 118 on the node: facts read over real HTTP (a Hugging Face and an Ollama registry that answer as theirs
/// do), the option chosen turned into the pull the node already runs, and the context carried to where each
/// engine keeps it (D4).
/// </summary>
public sealed class ModelFitTests : IAsyncLifetime
{
    private const string Repo = "someone/Moe-GGUF";

    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-fit-" + Guid.NewGuid().ToString("N"));
    private FakeHuggingFace hub = null!;
    private WebApplication registry = null!;
    private string registryUrl = null!;

    private static readonly byte[] Q4 = GgufWriter.Llama(arch: "qwen3moe", layers: 8, heads: 16, kvHeads: 4, keyLength: 128, context: 32768, experts: 64, denseBytes: 2048, expertBytes: 8192);
    private static readonly byte[] Q8 = GgufWriter.Llama(arch: "qwen3moe", layers: 8, heads: 16, kvHeads: 4, keyLength: 128, context: 32768, experts: 64, denseBytes: 4096, expertBytes: 16384);

    public async Task InitializeAsync()
    {
        hub = await FakeHuggingFace.StartAsync();
        hub.Repo(Repo,
            ("README.md", "# moe"u8.ToArray()),
            ("Moe-Q4_K_M.gguf", Q4),
            ("Q8_0/Moe-Q8_0-00001-of-00002.gguf", Q8),
            ("Q8_0/Moe-Q8_0-00002-of-00002.gguf", FakeHuggingFace.Bytes(1000, 1)),
            ("mmproj-Moe-f16.gguf", FakeHuggingFace.Bytes(500, 2)));
        hub.Info[Repo] = new { id = Repo, pipeline_tag = "text-generation", gguf = new { total = 30_000_000_000L, context_length = 32768 } };
        hub.Repo("someone/Moe-Checkpoint",
            ("config.json", """{"model_type":"olmoe","num_hidden_layers":16,"num_attention_heads":16,"num_key_value_heads":16,"hidden_size":2048,"num_experts":64,"num_experts_per_tok":8,"intermediate_size":1024,"max_position_embeddings":4096}"""u8.ToArray()),
            ("model-00001-of-00001.safetensors", FakeHuggingFace.Bytes(4000, 3)));
        hub.Info["someone/Moe-Checkpoint"] = new { id = "someone/Moe-Checkpoint", pipeline_tag = "text-generation", safetensors = new { total = 6_900_000_000L } };
        hub.Repo("someone/Pictures", ("model_index.json", "{}"u8.ToArray()), ("unet/diffusion_pytorch_model.safetensors", FakeHuggingFace.Bytes(100, 4)));
        hub.Info["someone/Pictures"] = new { id = "someone/Pictures", pipeline_tag = "text-to-image" };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        registry = builder.Build();
        var model = GgufWriter.Llama(arch: "qwen2", layers: 28, heads: 28, kvHeads: 4, keyLength: 128, context: 32768);
        var config = """{"model_format":"gguf","model_family":"qwen2","model_type":"7.6B","file_type":"Q4_K_M"}"""u8.ToArray();
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(model));
        var configDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(config));

        registry.MapGet("/v2/library/qwen2.5/manifests/7b", () => Results.Json(new
        {
            schemaVersion = 2,
            config = new { digest = configDigest, size = config.Length },
            layers = new[]
            {
                new { mediaType = "application/vnd.ollama.image.model", digest, size = (long)model.Length },
                new { mediaType = "application/vnd.ollama.image.template", digest = "sha256:t", size = 10L }
            }
        }));
        registry.MapGet("/v2/library/{name}/blobs/{blob}", (string blob) =>
            blob == digest ? Results.Bytes(model, "application/octet-stream") : blob == configDigest ? Results.Bytes(config, "application/json") : Results.NotFound());

        await registry.StartAsync();
        registryUrl = registry.Urls.First() + "/";
    }

    public async Task DisposeAsync()
    {
        await hub.DisposeAsync();
        await registry.StopAsync();
        await registry.DisposeAsync();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private ModelFactsReader Reader(bool huggingFace = true)
        => new(huggingFace ? () => hub.Client() : null, new HttpClient(), new Uri(registryUrl));

    private static MachineFacts Machine(bool llamacpp = true, bool ollama = true) =>
        new([new GpuFacts(0, "card", 24576, 24000)], 65536, 60000, 16,
        [
            new EngineFacts(FitEngines.Ollama, "ollama", ollama, ollama ? null : "this node has no Ollama engine", null, null, null, null),
            new EngineFacts(FitEngines.LlamaCpp, "gguf", llamacpp, llamacpp ? null : "this node has no llama.cpp engine", "/m", 1 << 20, null, null),
            new EngineFacts(FitEngines.Colibri, null, false, "this node has no colibri engine", null, null, null, null),
            new EngineFacts(FitEngines.Strata, null, false, "this node has no Strata install", null, null, null, null)
        ], []);

    // ---- D2: facts ----------------------------------------------------------------------------

    [Fact]
    public async Task AGgufRepoIsReadFromItsHeaderWithItsQuantsGroupedAndSplitShardsTogether()
    {
        var facts = await Reader().ReadAsync(Link(Repo), CancellationToken.None);

        Assert.Equal(ModelFacts.SourceGguf, facts.Source);
        Assert.Equal("qwen3moe", facts.Architecture);
        Assert.Equal(30_000_000_000L, facts.Parameters);
        Assert.Equal(8L * 4 * 256 * 2, facts.KvBytesPerToken);
        Assert.Equal(64, facts.Experts);
        Assert.True(facts.ExpertShare > 0.5);

        Assert.Equal(["Q4_K_M", "Q8_0"], facts.Variants.Select(v => v.Name).Order());
        var q8 = facts.Variants.Single(v => v.Name == "Q8_0");
        Assert.Equal(2, q8.Files);
        Assert.Equal(Q8.Length + 1000 + 500, q8.Bytes); // both shards and the projector
        Assert.Equal($"{Repo}:Q8_0", q8.Link);

        // The header came from a Range read of the smallest variant, not a download.
        Assert.Contains(hub.Ranges, r => r.StartsWith("bytes=0-"));
    }

    [Fact]
    public async Task ALinkWithAQuantNarrowsTheVariants()
    {
        var facts = await Reader().ReadAsync(Link(Repo + ":Q4_K_M"), CancellationToken.None);

        Assert.Equal(["Q4_K_M"], facts.Variants.Select(v => v.Name));
    }

    [Fact]
    public async Task ACheckpointIsReadFromItsConfig()
    {
        var facts = await Reader().ReadAsync(Link("someone/Moe-Checkpoint"), CancellationToken.None);

        Assert.Equal(ModelFacts.SourceCheckpoint, facts.Source);
        Assert.Equal(6_900_000_000L, facts.Parameters);
        Assert.Equal(64, facts.Experts);
        Assert.Equal("someone/Moe-Checkpoint", facts.Variants.Single().Link);
    }

    [Fact]
    public async Task APictureModelIsNamedForWhatItIsAndOffersNothing()
    {
        var facts = await Reader().ReadAsync(Link("someone/Pictures"), CancellationToken.None);

        Assert.Empty(facts.Variants);
        Assert.Contains(facts.Notes, n => n.Contains("text-to-image"));
    }

    [Fact]
    public async Task AnOllamaModelIsReadFromTheRegistryManifestAndItsBlobsHeader()
    {
        var facts = await Reader(huggingFace: false).ReadAsync(Link("qwen2.5:7b"), CancellationToken.None);

        Assert.Equal(ModelFacts.SourceOllama, facts.Source);
        Assert.Equal(7_600_000_000L, facts.Parameters);
        Assert.Equal(28, facts.Layers);
        Assert.Equal(28L * 4 * 256 * 2, facts.KvBytesPerToken);
        Assert.Equal("qwen2.5:7b", facts.Variants.Single().Link);
    }

    [Fact]
    public async Task HuggingFaceIsNotAskedWhenTheOwnerHasNotAllowedIt()
    {
        var refused = await Assert.ThrowsAsync<FitRefusedException>(() => Reader(huggingFace: false).ReadAsync(Link(Repo), CancellationToken.None));

        Assert.Equal(403, refused.Status);
        Assert.Contains("HuggingFace:Enabled", refused.Message);
    }

    [Theory]
    [InlineData("7.6B", 7_600_000_000L)]
    [InlineData("494.03M", 494_030_000L)]
    [InlineData("1.2T", 1_200_000_000_000L)]
    [InlineData("big", null)]
    public void OllamasSizeLabelIsParameters(string text, long? expected) => Assert.Equal(expected, ModelFactsReader.ParseSize(text));

    // ---- D5: the install is the pull the node already runs -----------------------------------

    [Fact]
    public async Task AnOllamaChoiceForAHuggingFaceGgufIsOllamasOwnPullOfIt()
    {
        var service = new FitService(Reader(), () => Machine());

        var command = await service.CommandAsync(Repo, FitEngines.Ollama, "Q4_K_M", 16384, false, CancellationToken.None);

        Assert.Equal(ModelCommand.KindPull, command.Kind);
        Assert.Equal($"hf.co/{Repo}:Q4_K_M", command.ModelName);
        Assert.Equal("ollama", command.Engine);
        Assert.Equal(16384, command.Context);
    }

    [Fact]
    public async Task ALlamaCppChoiceIsTheStoresPullWithTheContext()
    {
        var service = new FitService(Reader(), () => Machine());

        var command = await service.CommandAsync(Repo, FitEngines.LlamaCpp, "Q8_0", 8192, false, CancellationToken.None);

        Assert.Equal(ModelCommand.EngineHuggingFace, command.Engine);
        Assert.Equal($"{Repo}:Q8_0", command.ModelName);
        Assert.Equal(8192, command.Context);
    }

    [Fact]
    public async Task AnEngineTheNodeLacksOrOneThatCannotLoadTheModelIsRefusedWithItsReason()
    {
        var service = new FitService(Reader(), () => Machine(llamacpp: false));

        var lacking = await Assert.ThrowsAsync<FitRefusedException>(() => service.CommandAsync(Repo, FitEngines.LlamaCpp, "Q4_K_M", 8192, false, CancellationToken.None));
        var cannot = await Assert.ThrowsAsync<FitRefusedException>(() => service.CommandAsync(Repo, FitEngines.Colibri, null, 8192, false, CancellationToken.None));

        Assert.Equal(409, lacking.Status);
        Assert.Contains("no llama.cpp engine", lacking.Message);
        Assert.Equal(400, cannot.Status);
        Assert.Contains("not a GGUF", cannot.Message);
    }

    [Fact]
    public async Task AnOptionTheEstimateSaysDoesNotFitNeedsForce()
    {
        // Ollama does not pull a split GGUF: the Q8 is not a fit for it.
        var service = new FitService(Reader(), () => Machine());

        var refused = await Assert.ThrowsAsync<FitRefusedException>(() => service.CommandAsync(Repo, FitEngines.Ollama, "Q8_0", 8192, false, CancellationToken.None));

        Assert.Equal(409, refused.Status);
        Assert.Contains("force=true", refused.Message);
    }

    [Fact]
    public async Task TheFactsAreAskedOnceWhileTheOwnerTriesContexts()
    {
        var service = new FitService(Reader(), () => Machine());

        await service.EstimateAsync(Repo, 4096, CancellationToken.None);
        var before = hub.Ranges.Count;
        var answer = await service.EstimateAsync(Repo, 65536, CancellationToken.None);

        Assert.Equal(before, hub.Ranges.Count);
        Assert.Equal(65536, answer.Context);
        Assert.Contains(answer.Refused, r => r.Engine == FitEngines.Colibri);
    }

    // ---- D4: the context goes where each engine keeps it -------------------------------------

    [Fact]
    public void AnOllamaRequestGetsTheChosenContextUnlessItSetsOne()
    {
        var contexts = new ModelContexts(Path.Combine(root, ModelContexts.FileName));
        contexts.Set("Qwen2.5:7B", 16384);

        var plain = JsonNode.Parse(contexts.Apply("""{"model":"qwen2.5:7b","messages":[]}"""))!;
        var own = JsonNode.Parse(contexts.Apply("""{"model":"qwen2.5:7b","options":{"num_ctx":2048,"temperature":0}}"""))!;
        var other = contexts.Apply("""{"model":"llama3"}""");

        Assert.Equal(16384, plain["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(2048, own["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal("""{"model":"llama3"}""", other);

        // Kept across a restart, and :latest is the bare name.
        var again = new ModelContexts(contexts.FilePath);
        again.Set("llama3:latest", 8192);
        Assert.Equal(16384, again.For("qwen2.5:7b"));
        Assert.Equal(8192, again.For("llama3"));
    }

    [Fact]
    public void TheRoutersPresetCarriesTheChosenContextForADownloadedModel()
    {
        var models = Path.Combine(root, "gguf");
        Directory.CreateDirectory(Path.Combine(models, "tiny-q4"));
        Directory.CreateDirectory(Path.Combine(models, "configured"));
        Directory.CreateDirectory(Path.Combine(models, "by-hand"));
        File.WriteAllText(Path.Combine(models, "tiny-q4", HuggingFaceStore.Marker), """{"Repo":"a/b","Revision":"main","Commit":"c","Engine":"gguf","Files":[],"At":"2026-10-26T00:00:00Z","Context":4096}""");
        File.WriteAllText(Path.Combine(models, "configured", HuggingFaceStore.Marker), """{"Repo":"a/c","Revision":"main","Commit":"c","Engine":"gguf","Files":[],"At":"2026-10-26T00:00:00Z","Context":4096}""");

        var engine = new EngineOptions { Type = "llamacpp" };
        engine.Serve.ModelsDir = models;
        engine.Serve.Presets["configured"] = new LlamaCppPresetOptions { Settings = { ["ctx-size"] = "1024" } };

        var ini = LlamaCppServe.PresetIni(engine);

        Assert.Contains("[tiny-q4]\nctx-size = 4096\n", ini);
        Assert.Contains("[configured]\nctx-size = 1024\n", ini); // the configuration's own wins
        Assert.DoesNotContain("by-hand", ini);
        Assert.NotNull(LlamaCppServe.WritePresets("gguf", engine));
    }

    [Fact]
    public void StrataSetupIsGivenTheChosenContextOverTheConfiguredOne()
    {
        var options = new StrataOptions { Root = root, Python = "py" };
        options.Install.Context = 65536;

        var info = StrataInstaller.StartInfo(options, new HuggingFaceOptions { Enabled = true }, "coder", "IQ1_M", context: 16384);

        var i = info.ArgumentList.IndexOf("--context");
        Assert.Equal("16384", info.ArgumentList[i + 1]);
        Assert.Single(info.ArgumentList, a => a == "--context");
    }

    [Fact]
    public async Task AGgufPulledWithAContextKeepsItAndASecondPullChangesItAndRestartsTheRouter()
    {
        var restarts = new List<string>();
        var dir = Path.Combine(root, "router");
        var store = new HuggingFaceStore(
            new HuggingFaceOptions { Enabled = true, Endpoint = hub.Url },
            new HuggingFaceTargets("gguf", dir, null),
            () => hub.Client(),
            null,
            (engine, around, ct) => { restarts.Add(engine); return around?.Invoke() ?? Task.CompletedTask; },
            null,
            NullLogger.Instance);

        await foreach (var _ in store.PullAsync(Repo + ":Q4_K_M", 4096, CancellationToken.None))
        {
        }

        Assert.Equal(4096, HuggingFaceStore.ContextOf(Path.Combine(dir, "Moe-Q4_K_M")));

        var frames = new List<string>();

        await foreach (var frame in store.PullAsync(Repo + ":Q4_K_M", 8192, CancellationToken.None))
        {
            frames.Add(frame.Status);
        }

        Assert.Equal(8192, HuggingFaceStore.ContextOf(Path.Combine(dir, "Moe-Q4_K_M")));
        Assert.Contains(frames, f => f.Contains("8192-token context"));
        Assert.Equal(2, restarts.Count);
    }

    // ---- D6: an engine that plans its own placement is asked ---------------------------------

    /// <summary>A trimmed copy of what <c>coli plan --json</c> answered for GLM-5.3-Flash at 32K with COLI_VULKAN=1 on the 3090 Ti box.</summary>
    private const string ColiPlan = """
        {"tiers":{"disk":{"cold_expert_bytes":0},"ram":{"budget_bytes":185010331320,"dense_on_device_bytes":3752853504,"sequence_state_bytes":1107296256},
         "vram":{"budget_bytes":21840423936,"hot_expert_bytes":21828206592,"devices":[{"free_bytes":23840423936}]}},"model":{"model_bytes":194727335256},
         "projected_hit_rate":1,"expected_bottleneck":"CPU expert tail and GPU compute","warnings":["a warning"]}
        """;

    private static ModelFacts Checkpoint() => new(ModelFacts.SourceCheckpoint, "zai-org/GLM-5.3-Flash", "glm5", null, 321_000_000_000, 45, 45, 512, 1 << 20, 288, 8, 0.95,
        [new ModelVariant("checkpoint", 600_000_000_000, Link: "zai-org/GLM-5.3-Flash")], []);

    [Fact]
    public void ColibrisPlanIsTheOptionCappedAtTheCardsBudget()
    {
        using var plan = JsonDocument.Parse(ColiPlan);
        var engine = new EngineFacts(FitEngines.Colibri, "colibri", true, null, "/m", null, null, null, Accelerator: "vulkan");

        var option = ColibriPlanner.Map(plan.RootElement, Checkpoint(), engine, 32768, "E:/glm");

        Assert.Equal(FitPlacements.Ram, option.Placement);
        Assert.Equal("medium", option.Speed);
        Assert.Equal((23840423936 - (1L << 30)) / (1 << 20), option.VramMiB); // dense 3.5 + hot 20.3 GiB: more than the card, so the card less its reserve
        Assert.Equal(1056, option.KvMiB);
        Assert.Contains(option.Why, w => w.StartsWith("colibri's own plan"));
        Assert.Contains(option.Why, w => w == "colibri warns: a warning");
    }

    [Fact]
    public void ColibrisPlanOnACpuOnlyNodeSaysHowToUseTheCard()
    {
        using var plan = JsonDocument.Parse(ColiPlan);
        var engine = new EngineFacts(FitEngines.Colibri, "colibri", true, null, "/m", null, null, null);

        var option = ColibriPlanner.Map(plan.RootElement, Checkpoint(), engine, 32768, "E:/glm");

        Assert.Equal(0, option.VramMiB);
        Assert.Equal("slow", option.Speed);
        Assert.Contains(option.Why, w => w.Contains("Colibri:Serve:Vulkan=true"));
    }

    [Fact]
    public void ACheckpointAlreadyInTheCatalogueIsFoundByItsRepoName()
    {
        var dir = Path.Combine(root, "colibri", "glm-5.3-flash");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), "{}");

        var byDir = new ColibriPlanner(new Node.Backends.Colibri.ColibriOptions { Serve = { ModelsDir = Path.Combine(root, "colibri") } });
        var byName = new ColibriPlanner(new Node.Backends.Colibri.ColibriOptions { Serve = { Models = { ["glm-5.3-flash"] = dir } } });

        Assert.Equal(dir, byDir.ConvertedDirectory("zai-org/GLM-5.3-Flash"));
        Assert.Equal(dir, byName.ConvertedDirectory("zai-org/GLM-5.3-Flash"));
        Assert.Null(byDir.ConvertedDirectory("someone/Other"));
    }

    /// <summary>A trimmed copy of what Strata's own <c>model_table</c>/<c>recommend</c> answered on the same box.</summary>
    private const string StrataTable = """
        {"recommend":{"family":"qwen","model":"IQ3_S","context":131072,"why":"the 64 GB tier"},
         "table":[{"family":"qwen","sizes":[
           {"model":"Q2_0","download_gb":66.4,"ram_needed_gb":48,"experts_gb":34.0,"memory_gb":35.0,"on_this_pc":"fits"},
           {"model":"IQ3_S","download_gb":83.6,"ram_needed_gb":62,"experts_gb":50.3,"memory_gb":51.3,"on_this_pc":"fits"},
           {"model":"IQ3_XXS","download_gb":75.8,"ram_needed_gb":60,"experts_gb":42.9,"memory_gb":43.9,"on_this_pc":"fits in the low-RAM mode (slower: the GPU holds part of the experts)"},
           {"model":"IQ2_XS","download_gb":68.0,"ram_needed_gb":48,"experts_gb":35.5,"memory_gb":36.5,"on_this_pc":"does not fit"}]}]}
        """;

    [Fact]
    public void StratasOwnVerdictAndRecommendationAreTheOptions()
    {
        using var table = JsonDocument.Parse(StrataTable);
        var family = Shared.Strata.StrataModels.Families.First(f => f.Name == "qwen");
        var model = new ModelFacts(ModelFacts.SourceStrata, "q", "strata", null, 177_000_000_000, null, null, null, null, null, null, null,
            family.Sizes.Select(s => new ModelVariant(s.Name, 70_000_000_000, Link: Shared.Strata.StrataModels.Link(family, s))).ToArray(), []);
        var engine = new EngineFacts(FitEngines.Strata, "strata", true, null, "E:/strata", null, 20000, null);

        var options = StrataPlanner.Map(table.RootElement, model, Machine(), engine, 262144);

        var best = options.OrderByDescending(o => o.Score).First();
        Assert.Equal("IQ3_S", best.Variant);
        Assert.Contains(best.Why, w => w.StartsWith("Strata recommends this size here"));
        Assert.Contains(best.Why, w => w.Contains("131,072-token context"));
        Assert.Equal(20000, best.VramMiB); // the owner's limit, under the card less Strata's reserve
        Assert.Equal("slow", options.Single(o => o.Variant == "IQ3_XXS").Speed);
        Assert.False(options.Single(o => o.Variant == "IQ2_XS").Fits);
    }

    [Theory]
    [InlineData("fits", true, "strata", "medium")]
    [InlineData("tight", true, "strata", "slow")]
    [InlineData("part of its experts in RAM, the rest read from the SSD", true, "stream-from-disk", "slow")]
    [InlineData("slow here: part of its experts in RAM, the rest read from the SSD (setup asks first)", true, "stream-from-disk", "very slow")]
    [InlineData("does not fit", false, "none", "—")]
    public void StratasWordsAreAPlacement(string verdict, bool fits, string placement, string speed)
        => Assert.Equal((fits, placement, speed), StrataPlanner.Verdict(verdict));

    [Fact]
    public void ColiServeIsGivenTheChosenContextAndVulkan()
    {
        var dir = Path.Combine(root, "glm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, HuggingFaceStore.Marker), """{"Repo":"a/b","Revision":"main","Commit":"c","Engine":"colibri","Files":[],"At":"2026-10-26T00:00:00Z","Context":32768}""");

        var info = Node.Backends.Colibri.ColibriServe.StartInfo(new Node.Backends.Colibri.ColibriOptions { Serve = { Vulkan = true } }, dir, "glm", 18000);
        var plain = Node.Backends.Colibri.ColibriServe.StartInfo(new Node.Backends.Colibri.ColibriOptions(), Path.Combine(root, "none"), "x", 18000);

        Assert.Equal("32768", info.ArgumentList[info.ArgumentList.IndexOf("--ctx") + 1]);
        Assert.Equal("1", info.Environment["COLI_VULKAN"]);
        Assert.DoesNotContain("--ctx", plain.ArgumentList);
        Assert.Equal(Environment.GetEnvironmentVariable("COLI_VULKAN"), plain.Environment.TryGetValue("COLI_VULKAN", out var v) ? v : null);
    }

    private static ModelLink Link(string text)
    {
        Assert.True(ModelLink.TryParse(text, out var link, out var error), error);
        return link!;
    }
}
