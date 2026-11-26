using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Configuration;
using InferHub.Node.Fit;
using InferHub.Node.Tools;
using InferHub.Shared.Contracts;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 121: a picture or video model from a link, answered by the node's recipes. A recipe that pins the repository
/// is the option; a repository whose pipeline a recipe here runs gets a recipe written from that one, only at install
/// and only after the owner's terms; code, pickled weights and pipelines no recipe runs are said, not offered.
/// </summary>
public sealed class RecipeFromLinkTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inferhub-recipes-" + Guid.NewGuid().ToString("N"));
    private string Shipped => Path.Combine(root, "shipped");
    private string Added => Path.Combine(root, "added");

    private FakeHuggingFace hub = null!;
    private readonly ToolOptions tools = new() { Enabled = true, AllowModelDownload = true };
    private readonly NodeOptions node = new();
    private readonly Runtime runtime = new();

    private static byte[] Weights(int length) => FakeHuggingFace.Bytes(length, length);

    private static byte[] Index(string pipeline, string unetLibrary = "diffusers") => Encoding.UTF8.GetBytes($$"""
        {"_class_name": "{{pipeline}}", "_diffusers_version": "0.36.0",
         "unet": ["{{unetLibrary}}", "UNet2DConditionModel"], "vae": ["diffusers", "AutoencoderKL"],
         "text_encoder": ["transformers", "CLIPTextModel"], "tokenizer": ["transformers", "CLIPTokenizer"],
         "scheduler": ["diffusers", "PNDMScheduler"], "safety_checker": ["stable_diffusion", "StableDiffusionSafetyChecker"],
         "feature_extractor": [null, null]}
        """);

    /// <summary>A Stable Diffusion repository: full files, fp16 files when asked, a safety checker no recipe loads.</summary>
    private void Pipeline(string repo, string pipeline, int unet, bool fp16 = true, params (string, byte[])[] extra)
    {
        var files = new List<(string, byte[])>
        {
            ("model_index.json", Index(pipeline)),
            ("unet/config.json", "{}"u8.ToArray()),
            ("unet/diffusion_pytorch_model.safetensors", Weights(unet * 2)),
            ("vae/diffusion_pytorch_model.safetensors", Weights(200)),
            ("text_encoder/model.safetensors", Weights(400)),
            ("tokenizer/vocab.json", "{}"u8.ToArray()),
            ("safety_checker/model.safetensors", Weights(9000)),
            ("v1-5-pruned.safetensors", Weights(7000))
        };

        if (fp16)
        {
            files.Add(("unet/diffusion_pytorch_model.fp16.safetensors", Weights(unet)));
            files.Add(("vae/diffusion_pytorch_model.fp16.safetensors", Weights(100)));
            files.Add(("text_encoder/model.fp16.safetensors", Weights(200)));
        }

        files.AddRange(extra);
        hub.Repo(repo, [.. files]);
    }

    public async Task InitializeAsync()
    {
        hub = await FakeHuggingFace.StartAsync();
        Directory.CreateDirectory(Shipped);
        tools.Image.RecipeDirectory = Shipped;
        tools.Image.AddedRecipeDirectory = Added;
        tools.Image.RequireGpu = true;

        // The template: sd15 as it ships, fp16, 1000-byte UNet (1300 bytes of fp16 weights in all).
        Recipe("sd15", "base/sd15", "StableDiffusionPipeline", 4000, variant: "fp16");
        Recipe("sdxl-pinned", "someone/Pinned", "StableDiffusionXLPipeline", 8000, variant: "fp16");
        Pipeline("base/sd15", "StableDiffusionPipeline", 1000);
        Pipeline("someone/Pinned", "StableDiffusionXLPipeline", 1000);
        hub.Info["someone/Pinned"] = new { id = "someone/Pinned", pipeline_tag = "text-to-image", library_name = "diffusers", cardData = new { license = "openrail++" }, gated = false };

        // A fine-tune twice the template's size: 2000 + 100 + 200 = 2300 of fp16 against 1300.
        Pipeline("someone/Dream-8", "StableDiffusionPipeline", 2000);
        hub.Info["someone/Dream-8"] = new { id = "someone/Dream-8", pipeline_tag = "text-to-image", library_name = "diffusers", cardData = new { license = "creativeml-openrail-m" }, gated = false };
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

    private void Recipe(string id, string repo, string pipeline, int vram, string? variant = null) =>
        File.WriteAllText(Path.Combine(Shipped, id + ".json"), new JsonObject
        {
            ["id"] = id,
            ["repo"] = repo,
            ["revision"] = FakeHuggingFace.Commit,
            ["pipeline"] = pipeline,
            ["license"] = new JsonObject { ["id"] = "CreativeML-OpenRAIL-M", ["permissive"] = true, ["url"] = "https://example/licence" },
            ["operations"] = new JsonArray("generate", "edit", "variation"),
            ["defaults"] = new JsonObject { ["steps"] = 30, ["guidance"] = 7.5, ["size"] = "512x512" },
            ["sizes"] = new JsonArray("512x512", "768x768"),
            ["maxSteps"] = 75,
            ["vramMiB"] = vram,
            ["variant"] = variant,
            ["dtype"] = "float16",
            ["quantization"] = "none",
            ["cpuViable"] = true,
            ["notes"] = "the template's own notes"
        }.ToJsonString());

    private static MachineFacts Machine(bool gpu = true) =>
        new(gpu ? [new GpuFacts(0, "card", 24576, 24000)] : [], 65536, 60000, 16,
        [
            new EngineFacts(FitEngines.Ollama, "ollama", true, null, null, null, null, null),
            new EngineFacts(FitEngines.LlamaCpp, "gguf", true, null, "/m", 1 << 20, null, null)
        ], []);

    private FitService Fit(bool gpu = true)
    {
        var recipes = new RecipeFit(runtime, TestOptions.Monitor(tools), TestOptions.Monitor(node), () => hub.Client(), NullLogger<RecipeFit>.Instance);
        return new FitService(new ModelFactsReader(() => hub.Client(), new HttpClient(), new Uri("http://127.0.0.1:9/")), () => Machine(gpu), recipes: recipes);
    }

    // ---- reading a diffusers repository -------------------------------------------------------

    [Theory]
    [InlineData("unet/diffusion_pytorch_model.safetensors", null)]
    [InlineData("text_encoder/model.fp16.safetensors", "fp16")]
    [InlineData("unet/diffusion_pytorch_model.fp16-00001-of-00002.safetensors", "fp16")]
    [InlineData("unet/diffusion_pytorch_model-00001-of-00002.safetensors", null)]
    [InlineData("vae/diffusion_pytorch_model.bf16.safetensors", "bf16")]
    public void AVariantIsWhatDiffusersVariantPicks(string path, string? variant) => Assert.Equal(variant, DiffusersRepo.Variant(path));

    [Fact]
    public async Task ARepositoryIsReadAsItsPipelineItsComponentsWeightsAndItsCard()
    {
        var facts = await Fit().EstimateAsync("someone/Dream-8", null, CancellationToken.None);
        var pipeline = facts.Model.Pipeline!;

        Assert.Equal(ModelFacts.SourceDiffusers, facts.Model.Source);
        Assert.Equal("StableDiffusionPipeline", pipeline.ClassName);
        Assert.Equal(FakeHuggingFace.Commit, pipeline.Commit);
        Assert.Equal("creativeml-openrail-m", pipeline.License);

        // The components a recipe loads — not the safety checker it passes as None, not the single-file checkpoint at the root.
        Assert.Equal(4000 + 200 + 400, pipeline.Bytes);
        Assert.Equal(2000 + 100 + 200, pipeline.Fp16Bytes);
        Assert.Null(pipeline.Refusal);
    }

    // ---- a recipe here pins the repository ----------------------------------------------------

    [Fact]
    public async Task ALinkToARepositoryARecipePinsIsThatRecipeAndItsPullIsPhase48s()
    {
        var service = Fit();
        var answer = await service.EstimateAsync("https://huggingface.co/someone/Pinned", null, CancellationToken.None);

        var option = Assert.Single(answer.Options);
        Assert.Equal(FitEngines.Diffusion, option.Engine);
        Assert.Equal("sdxl-pinned", option.Variant);
        Assert.True(option.Available && option.Fits);
        Assert.Equal(8000, option.VramMiB);
        Assert.Contains(option.Why, w => w.Contains("recipe 'sdxl-pinned' on this node pins someone/Pinned"));
        Assert.Empty(answer.Refused);

        var plan = await service.PlanAsync("someone/Pinned", FitEngines.Diffusion, "sdxl-pinned", null, false, CancellationToken.None);
        Assert.Null(plan.Recipe);
        Assert.Equal(ModelCommand.KindPull, plan.Command.Kind);
        Assert.Equal("sdxl-pinned", plan.Command.ModelName);
        Assert.Equal(RecipeFit.Tool, plan.Command.Tool);
        Assert.Null(plan.Command.Engine);
    }

    // ---- a recipe written from one that runs the same pipeline --------------------------------

    [Fact]
    public async Task AFineTuneGetsARecipeWrittenFromTheOneThatRunsItsPipelineWithItsVramScaledByItsWeights()
    {
        var service = Fit();
        var answer = await service.EstimateAsync("someone/Dream-8", null, CancellationToken.None);

        var option = Assert.Single(answer.Options);
        Assert.Equal("dream-8", option.Variant);
        Assert.True(option.Available, string.Join(" | ", option.Why));
        Assert.True(option.Fits);

        // 4000 MiB × 2300 / 1300 bytes, rounded up to a hundred.
        Assert.Equal(7100, option.VramMiB);
        Assert.Contains(option.Why, w => w.Contains("a recipe written from 'sd15'"));
        Assert.Contains(option.Why, w => w.Contains("4000 MiB ×"));

        // Nothing is written by looking.
        Assert.False(Directory.Exists(Added) && Directory.EnumerateFiles(Added).Any());

        var command = await service.CommandAsync("someone/Dream-8", FitEngines.Diffusion, "dream-8", null, false, CancellationToken.None);
        Assert.Equal("dream-8", command.ModelName);
        Assert.Equal(RecipeFit.Tool, command.Tool);
        Assert.Equal(1, runtime.Reloads);

        var written = JsonNode.Parse(File.ReadAllText(Path.Combine(Added, "dream-8.json")))!.AsObject();
        Assert.Equal("dream-8", (string?)written["id"]);
        Assert.Equal("sd15", (string?)written["derivedFrom"]);
        Assert.Equal("someone/Dream-8", (string?)written["repo"]);
        Assert.Equal(FakeHuggingFace.Commit, (string?)written["revision"]);
        Assert.Equal("StableDiffusionPipeline", (string?)written["pipeline"]);
        Assert.Equal("fp16", (string?)written["variant"]);
        Assert.True((bool?)written["useSafetensors"]);
        Assert.Equal(7100, (int?)written["vramMiB"]);
        Assert.Equal("CreativeML-OpenRAIL-M", (string?)written["license"]!["id"]);
        Assert.True((bool?)written["license"]!["permissive"]);
        Assert.Equal("https://huggingface.co/someone/Dream-8", (string?)written["license"]!["url"]);
        Assert.Equal(30, (int?)written["defaults"]!["steps"]);
        Assert.Equal(["512x512", "768x768"], written["sizes"]!.AsArray().Select(s => (string?)s));
        Assert.DoesNotContain("the template's own notes", (string?)written["notes"]);

        // Written once, it is a recipe here: the next look is the recipe that pins the repository.
        var again = await service.EstimateAsync("someone/Dream-8", null, CancellationToken.None);
        Assert.Contains(Assert.Single(again.Options).Why, w => w.Contains("recipe 'dream-8' on this node pins someone/Dream-8"));
    }

    [Fact]
    public async Task ARepositoryWithoutFp16FilesLoadsItsFullFilesAndAnIdThatIsTakenGetsItsOwner()
    {
        Pipeline("other/sd15", "StableDiffusionPipeline", 1000, fp16: false);
        var service = Fit();

        // A card that names no licence is not permissive: the owner accepts it before it installs.
        var option = Assert.Single((await service.EstimateAsync("other/sd15", null, CancellationToken.None)).Options);
        Assert.False(option.Available);
        Assert.Contains(option.Why, w => w.Contains("'unknown', which this node has not accepted") && w.Contains("Tools:Image:AcceptedLicenses"));

        tools.Image.AcceptedLicenses.Add("unknown");
        var plan = await service.PlanAsync("other/sd15", FitEngines.Diffusion, null, null, false, CancellationToken.None);

        Assert.Equal("other-sd15", plan.Command.ModelName);
        Assert.Null(plan.Recipe!["variant"]);
        Assert.Equal("unknown", (string?)plan.Recipe["license"]!["id"]);
        Assert.False((bool?)plan.Recipe["license"]!["permissive"]);

        // Counted at the stored size (2000 + 200 + 400 against 1300): more, never less, than the card will hold.
        Assert.Equal(8000, (int?)plan.Recipe["vramMiB"]);
    }

    [Fact]
    public async Task TheCardsBaseModelChoosesTheTemplateOverTheClosestSize()
    {
        Recipe("sd15-turbo", "base/sd15-turbo", "StableDiffusionPipeline", 4000, variant: "fp16");
        Pipeline("base/sd15-turbo", "StableDiffusionPipeline", 2000);
        Pipeline("someone/Turbo-Tune", "StableDiffusionPipeline", 1000);
        hub.Info["someone/Turbo-Tune"] = new { id = "someone/Turbo-Tune", cardData = new { license = "mit", base_model = "base/sd15-turbo" } };

        var plan = await Fit().PlanAsync("someone/Turbo-Tune", FitEngines.Diffusion, null, null, false, CancellationToken.None);

        Assert.Equal("sd15-turbo", (string?)plan.Recipe!["derivedFrom"]);
        Assert.Contains(plan.Option.Why, w => w.Contains("the card's base model"));
        Assert.True(plan.Option.Available);
    }

    // ---- said, not offered --------------------------------------------------------------------

    [Fact]
    public async Task CodeOrPickledWeightsInTheRepositoryAreRefusedAndNothingIsOffered()
    {
        Pipeline("someone/Code", "StableDiffusionPipeline", 1000, true, ("pipeline.py", "import os"u8.ToArray()));
        hub.Repo("someone/Custom",
            ("model_index.json", Index("StableDiffusionPipeline", unetLibrary: "my_unet")),
            ("unet/my_unet.py", "class UNet2DConditionModel: ..."u8.ToArray()),
            ("unet/diffusion_pytorch_model.safetensors", Weights(100)),
            ("vae/diffusion_pytorch_model.safetensors", Weights(100)),
            ("text_encoder/model.safetensors", Weights(100)));
        hub.Repo("someone/Pickled",
            ("model_index.json", Index("StableDiffusionPipeline")),
            ("unet/diffusion_pytorch_model.bin", Weights(100)),
            ("vae/diffusion_pytorch_model.safetensors", Weights(100)),
            ("text_encoder/model.safetensors", Weights(100)));

        var service = Fit();

        foreach (var (repo, words) in new[] { ("someone/Code", "Python code (pipeline.py)"), ("someone/Custom", "Python code (unet/my_unet.py)"), ("someone/Pickled", "only pickled weights") })
        {
            var answer = await service.EstimateAsync(repo, null, CancellationToken.None);
            Assert.Empty(answer.Options);
            Assert.Contains(words, Assert.Single(answer.Refused).Reason);
            await Assert.ThrowsAsync<FitRefusedException>(() => service.CommandAsync(repo, FitEngines.Diffusion, null, null, false, CancellationToken.None));
        }

        Assert.False(Directory.Exists(Added) && Directory.EnumerateFiles(Added).Any());
    }

    [Fact]
    public async Task APipelineNoRecipeHereRunsIsSaidWithTheOnesThatDo()
    {
        // Kolors names diffusers' own pipeline module as its text encoder's library: that is not code from the repository.
        hub.Repo("someone/Video",
            ("model_index.json", """{"_class_name": "HunyuanVideoPipeline", "text_encoder": ["kolors", "ChatGLMModel"], "transformer": ["diffusers", "HunyuanVideoTransformer3DModel"]}"""u8.ToArray()),
            ("text_encoder/model.safetensors", Weights(100)),
            ("transformer/diffusion_pytorch_model.safetensors", Weights(100)));

        var answer = await Fit().EstimateAsync("someone/Video", null, CancellationToken.None);

        Assert.Empty(answer.Options);
        var reason = Assert.Single(answer.Refused).Reason;
        Assert.Contains("no recipe on this node runs HunyuanVideoPipeline", reason);
        Assert.Contains("StableDiffusionPipeline, StableDiffusionXLPipeline", reason);
    }

    [Fact]
    public async Task AModelThatIsNeitherTextNorDiffusersIsNamedForWhatItIs()
    {
        hub.Repo("someone/Voice", ("config.json", "{}"u8.ToArray()), ("model.onnx", Weights(100)));
        hub.Info["someone/Voice"] = new { id = "someone/Voice", pipeline_tag = "text-to-speech", library_name = "onnx" };

        var answer = await Fit().EstimateAsync("someone/Voice", null, CancellationToken.None);

        Assert.Empty(answer.Options);
        Assert.Contains(answer.Model.Notes, n => n.Contains("text-to-speech model for onnx") && n.Contains("whisper and piper"));
    }

    // ---- the owner's switches ------------------------------------------------------------------

    [Fact]
    public async Task AGatedRepositoryWithALicenceNobodyAcceptedSaysBothKeysAndInstallsNothingUntilTheyAreSet()
    {
        Pipeline("acme/Gated", "StableDiffusionPipeline", 1000);
        hub.Info["acme/Gated"] = new { id = "acme/Gated", cardData = new { license = "other", license_name = "acme-licence" }, gated = "manual" };
        var service = Fit();

        var option = Assert.Single((await service.EstimateAsync("acme/Gated", null, CancellationToken.None)).Options);
        Assert.False(option.Available);
        Assert.Contains(option.Why, w => w.Contains("gated on Hugging Face") && w.Contains("Tools:Image:HuggingFaceToken"));
        Assert.Contains(option.Why, w => w.Contains("'acme-licence', which this node has not accepted"));
        await Assert.ThrowsAsync<FitRefusedException>(() => service.CommandAsync("acme/Gated", FitEngines.Diffusion, null, null, false, CancellationToken.None));

        tools.Image.AcceptedLicenses.Add("acme-licence");
        tools.Image.HuggingFaceToken = "hf_x";
        var plan = await service.PlanAsync("acme/Gated", FitEngines.Diffusion, null, null, false, CancellationToken.None);
        Assert.True(plan.Option.Available);
        Assert.True((bool?)plan.Recipe!["gated"]);
    }

    [Fact]
    public async Task WithoutTheDiffusionToolOrDownloadsNothingIsAvailableAndTheReasonComesFirst()
    {
        runtime.Tools = [];
        var option = Assert.Single((await Fit().EstimateAsync("someone/Dream-8", null, CancellationToken.None)).Options);
        Assert.False(option.Available);
        Assert.StartsWith("this node runs no 'diffusion' tool", option.Why[0]);

        runtime.Tools = ["diffusion"];
        tools.AllowModelDownload = false;
        option = Assert.Single((await Fit().EstimateAsync("someone/Dream-8", null, CancellationToken.None)).Options);
        Assert.False(option.Available);
        Assert.Contains("AllowModelDownload is false", option.Why[0]);
    }

    [Fact]
    public async Task OnACpuOnlyBoxARecipeTheCpuRunsFitsOnlyWhenTheWorkerMayStartThere()
    {
        var option = Assert.Single((await Fit(gpu: false).EstimateAsync("someone/Dream-8", null, CancellationToken.None)).Options);
        Assert.Equal(FitPlacements.Cpu, option.Placement);
        Assert.False(option.Fits);
        Assert.Contains(option.Why, w => w.Contains("RequireGpu is true"));

        tools.Image.RequireGpu = false;
        Assert.True(Assert.Single((await Fit(gpu: false).EstimateAsync("someone/Dream-8", null, CancellationToken.None)).Options).Fits);
    }

    [Fact]
    public async Task TheNodesVramBudgetIsTheGateWhenOneIsDeclared()
    {
        node.Vram.BudgetMiB = 8192;
        node.Vram.ReserveMiB = 2048;

        var option = Assert.Single((await Fit().EstimateAsync("someone/Dream-8", null, CancellationToken.None)).Options);

        Assert.False(option.Fits);
        Assert.Contains(option.Why, w => w.Contains("needs 7100 MiB") && w.Contains("budgets 6144 MiB"));
    }

    // ---- from the hub (120) --------------------------------------------------------------------

    [Fact]
    public async Task TheHubsInstallWritesTheRecipeOnlyAfterTheOwnersDiskTermsAllowIt()
    {
        var terms = new NodeOptions { Management = { AllowFromHub = true, MaxModelGiB = 0, KeepFreeGiB = 0 } };
        var fit = Fit();
        var management = new HubManagement(fit, new CatalogService(null, new HttpClient(), new Uri("http://127.0.0.1:9/"), fit.Machine), TestOptions.Monitor(terms), NullLogger<HubManagement>.Instance);
        ToolJob Install() => new(Guid.NewGuid(), ManagementJob.Capability, ManagementJob.Install,
            JsonSerializer.Serialize(new ManagementRequest("someone/Dream-8", null, FitEngines.Diffusion, "dream-8")));

        var refused = await management.RunAsync(Install(), CancellationToken.None);
        Assert.False(refused.Success);
        Assert.Equal(ManagementJob.OverQuota, refused.ErrorCode);
        Assert.False(Directory.Exists(Added) && Directory.EnumerateFiles(Added).Any());
        Assert.Equal(0, runtime.Reloads);

        terms.Management.MaxModelGiB = null;
        var done = await management.RunAsync(Install(), CancellationToken.None);
        Assert.True(done.Success, done.Error);

        var install = JsonSerializer.Deserialize<ManagementInstall>(done.Payload!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(RecipeFit.Tool, install.Command.Tool);
        Assert.Equal("dream-8", install.Command.ModelName);
        Assert.True(File.Exists(Path.Combine(Added, "dream-8.json")));
        Assert.Equal(1, runtime.Reloads);
    }

    // ---- what the worker is told -----------------------------------------------------------------

    [Fact]
    public void TheWorkerReadsTheShippedRecipesThenTheAddedOnes()
    {
        Assert.Equal(Shipped + Path.PathSeparator + Added, tools.WorkerEnvironment()["INFERHUB_IMAGE_RECIPES"]);
        Assert.Equal([Shipped, Added], tools.Image.RecipeDirectories());

        // An unset shipped directory stays the worker's own default: an empty first entry.
        var bare = new ToolOptions { Image = { AddedRecipeDirectory = Added } };
        Assert.Equal(Path.PathSeparator + Added, bare.WorkerEnvironment()["INFERHUB_IMAGE_RECIPES"]);
        Assert.Equal(string.Empty, new ToolOptions().WorkerEnvironment()["INFERHUB_IMAGE_RECIPES"]);
    }

    /// <summary>The tool runtime as far as recipes go: which tools run, and how often the catalogue was read again.</summary>
    private sealed class Runtime : IToolRuntime
    {
        public IReadOnlyList<string> Tools { get; set; } = ["diffusion"];

        public int Reloads { get; private set; }

        public bool Enabled => true;

        public IReadOnlyList<NodeCapability> Capabilities => [];

        public event Action? CapabilitiesChanged
        {
            add { }
            remove { }
        }

        public NodeToolState State(string nodeId) => throw new NotSupportedException();

        public Task<ToolWorkerLease> AcquireAsync(string capability, string model, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ToolWorkerLease> AcquireToolAsync(string toolId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<string> ToolIds => Tools;

        public IReadOnlyList<ImageRecipeInfo> ImageRecipes => [];

        public void SetDisabledModels(IReadOnlyCollection<string> models)
        {
        }

        public Task SetDisabledToolsAsync(IReadOnlyCollection<string> toolIds, CancellationToken cancellationToken) => Task.CompletedTask;

        public void ReloadRecipes() => Reloads++;
    }
}
