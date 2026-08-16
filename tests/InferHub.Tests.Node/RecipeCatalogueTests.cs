using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 48, D3/D5/D6. What the <em>node</em> reads out of a recipe file — the id, the licence and
/// the megabytes — and what it does with each.
/// </summary>
/// <remarks>
/// The node deliberately reads three fields and not the repo, the pipeline class or the aspect
/// buckets: those are the worker's business (phase-41 D1). What it needs locally is everything two
/// consumers need with no worker running — the profile clamp, which is pure, and the decision not to
/// <em>fetch</em> an unlicensed model, which has to precede the process that would fetch it.
/// </remarks>
public class RecipeCatalogueTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "inferhub-recipes-" + Guid.NewGuid().ToString("N"));

    public RecipeCatalogueTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(directory, name), json);

    private IReadOnlyDictionary<string, ImageRecipeInfo> Load() =>
        ImageRecipeCatalogue.LoadDirectory(directory, NullLogger.Instance);

    private const string Sdxl = """
        {
          "id": "sdxl",
          "repo": "stabilityai/stable-diffusion-xl-base-1.0",
          "revision": "462165984030d82259a11f4367a4eed129e94a7b",
          "license": { "id": "CreativeML-OpenRAIL++-M", "permissive": true, "url": "https://example.invalid/l" },
          "vramMiB": 8000,
          "quantization": "none"
        }
        """;

    private const string Turbo = """
        {
          "id": "sdxl-turbo",
          "repo": "stabilityai/sdxl-turbo",
          "revision": "71153311d3dbb46851df1931d3ca6e939de83304",
          "license": { "id": "sai-nc-community", "permissive": false, "url": "https://example.invalid/nc" },
          "vramMiB": 8000
        }
        """;

    /// <summary>
    /// The pin is not optional (phase-46 D3). Without it "which weights were in 3.16.0" has no
    /// answer — and a catalogue that counted a model the worker will never offer would budget VRAM
    /// for something that cannot run, and refuse a profile naming it for the wrong reason.
    /// </summary>
    [Fact]
    public void ARecipeWithNoRevisionIsSkipped()
    {
        Write("sdxl.json", Sdxl);
        Write("unpinned.json", """{ "id": "unpinned", "repo": "somebody/something", "vramMiB": 1000 }""");

        var catalogue = Load();

        Assert.True(catalogue.ContainsKey("sdxl"));
        Assert.False(catalogue.ContainsKey("unpinned"));
    }

    /// <summary>A broken file is skipped, never fatal — a node's inference must not depend on it.</summary>
    [Fact]
    public void AMalformedRecipeIsSkippedAndTheRestStillLoad()
    {
        Write("sdxl.json", Sdxl);
        Write("broken.json", "{ this is not json");

        Assert.Single(Load());
    }

    [Fact]
    public void ANonPermissiveRecipeIsLoadedAndNotLicensedUntilItsLicenceIsAccepted()
    {
        Write("sdxl-turbo.json", Turbo);

        var recipe = Load()["sdxl-turbo"];

        // Loaded — it is in the catalogue, with its licence and its link, so the log line and the
        // console can both name what is being refused and where to read it.
        Assert.False(recipe.Permissive);
        Assert.Equal("sai-nc-community", recipe.LicenseId);
        Assert.Equal("https://example.invalid/nc", recipe.LicenseUrl);

        Assert.False(recipe.IsLicensed([]));
        Assert.True(recipe.IsLicensed(["sai-nc-community"]));
        Assert.True(recipe.IsLicensed(["SAI-NC-Community"]));
    }

    /// <summary>
    /// <b>A blank entry is ignored, not counted</b> — the v3.10.0 bug, in a new list. An array that
    /// arrives from a container's environment cannot have an element removed; setting it to "" is
    /// the only lever <c>docker run</c> gives you, and a blank that granted a licence with a blank
    /// id would be a consent nobody typed.
    /// </summary>
    [Fact]
    public void ABlankEntryInAcceptedLicensesGrantsNothing()
    {
        Write("sdxl-turbo.json", Turbo);
        Write("nameless.json", """
            {
              "id": "nameless",
              "repo": "somebody/something",
              "revision": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
              "vramMiB": 1000
            }
            """);

        var catalogue = Load();

        Assert.False(catalogue["sdxl-turbo"].IsLicensed(["", "   "]));

        // A recipe that says nothing about its licence is treated as NOT permissive: a recipe that
        // forgot to say is one nobody has read the licence of, and the other default would make the
        // consent opt-out by accident of a missing field.
        Assert.Equal("unknown", catalogue["nameless"].LicenseId);
        Assert.False(catalogue["nameless"].Permissive);
        Assert.False(catalogue["nameless"].IsLicensed([""]));
        Assert.True(catalogue["nameless"].IsLicensed(["unknown"]));
    }

    [Fact]
    public void AcceptingOneLicenceDoesNotEnableAnother()
    {
        Write("sdxl-turbo.json", Turbo);
        Write("sd35.json", """
            {
              "id": "sd35-medium",
              "repo": "stabilityai/stable-diffusion-3.5-medium",
              "revision": "b940f670f0eda2d07fbb75229e779da1ad11eb80",
              "license": { "id": "stabilityai-ai-community", "permissive": false },
              "vramMiB": 16000
            }
            """);

        var catalogue = Load();
        string[] accepted = ["stabilityai-ai-community"];

        Assert.True(catalogue["sd35-medium"].IsLicensed(accepted));
        Assert.False(catalogue["sdxl-turbo"].IsLicensed(accepted));
    }

    /// <summary>The key on <c>ToolOptions</c> is the same grant, and it counts blanks out too.</summary>
    [Fact]
    public void ToolOptionsAcceptsALicenceByIdAndIgnoresBlanks()
    {
        var options = new ImageToolOptions { AcceptedLicenses = ["", "sai-nc-community", "  "] };

        Assert.True(options.AcceptsLicense("sai-nc-community"));
        Assert.True(options.AcceptsLicense("SAI-NC-COMMUNITY"));
        Assert.False(options.AcceptsLicense("stabilityai-ai-community"));
        Assert.False(options.AcceptsLicense(""));
    }

    /// <summary>A node with no image tool has no recipe directory, and that is not an error.</summary>
    [Fact]
    public void AnAbsentDirectoryIsAnEmptyCatalogue()
    {
        Assert.Empty(ImageRecipeCatalogue.LoadDirectory(null, NullLogger.Instance));
        Assert.Empty(ImageRecipeCatalogue.LoadDirectory(
            Path.Combine(directory, "nowhere"), NullLogger.Instance));
    }

    [Fact]
    public void QuantizationIsReadAndDefaultsToNone()
    {
        Write("sdxl.json", Sdxl);
        Write("flux.json", """
            {
              "id": "flux-schnell",
              "repo": "black-forest-labs/FLUX.1-schnell",
              "revision": "741f7c3ce8b383c54771c7003378a50191e9efe9",
              "license": { "id": "Apache-2.0", "permissive": true },
              "vramMiB": 12000,
              "quantization": "nf4"
            }
            """);

        var catalogue = Load();

        Assert.Equal("none", catalogue["sdxl"].Quantization);
        Assert.Equal("nf4", catalogue["flux-schnell"].Quantization);
    }

    /// <summary>
    /// The catalogue this release actually ships, parsed from the files in the repository. A recipe
    /// whose <c>vramMiB</c> or licence flag was wrong would put the whole gate on the wrong side of
    /// its own arithmetic, and nothing else in the suite reads these files.
    /// </summary>
    [Fact]
    public void TheShippedRecipesParseAndSayWhatTheDocsSayTheySay()
    {
        var shipped = ImageRecipeCatalogue.LoadDirectory(RepositoryRecipeDirectory(), NullLogger.Instance);

        Assert.Equal(
            ["flux-schnell", "qwen-360", "qwen-image", "sd15", "sd35-medium", "sdxl", "sdxl-turbo", "wan-t2v-1.3b"],
            shipped.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        // The two that need a licence decision, and only those two.
        var nonPermissive = shipped.Values.Where(r => !r.Permissive).Select(r => r.Id).OrderBy(id => id).ToArray();
        Assert.Equal(["sd35-medium", "sdxl-turbo"], nonPermissive);

        // The three that only exist because they are quantized.
        Assert.Equal("nf4", shipped["flux-schnell"].Quantization);
        Assert.Equal("nf4", shipped["qwen-image"].Quantization);
        Assert.Equal("nf4", shipped["qwen-360"].Quantization);

        // Phase 57's video recipe is in the SAME catalogue and therefore behind the same two gates.
        // `ImageRecipeCatalogue` reads three fields and does not know what a recipe produces —
        // deliberately (48 deviation 3) — which is what makes the licence check and the VRAM budget
        // cover a modality the class has never heard of.
        Assert.True(shipped["wan-t2v-1.3b"].Permissive);
        Assert.Equal("none", shipped["wan-t2v-1.3b"].Quantization);

        // …and every one of them fits a 24 GB card at the documented reserve, which is the claim
        // the README makes and the only place it is checked. Wan2.1's 15 500 MiB is the number that
        // makes this worth re-reading: "1.3B" names the transformer, and the ~11B UMT5 text encoder
        // beside it is what the budget is actually sized for.
        foreach (var recipe in shipped.Values)
        {
            Assert.True(
                VramBudget.Fits(24576, 2048, recipe.VramMiB),
                $"'{recipe.Id}' declares {recipe.VramMiB} MiB, which does not fit a 24 GB card with the default 2048 MiB reserve.");
        }
    }

    /// <summary>
    /// Phase 49: <c>qwen-360</c> is a LoRA over <c>qwen-image</c>'s base, and the worker's adapter
    /// swap only fires when the two agree on repo, revision, dtype and quantization.
    /// </summary>
    /// <remarks>
    /// Bumping one recipe's pin and not the other's does not fail anything — it silently turns every
    /// alternation between them into a full 20B reload, which is 40–90 s a caller pays and nobody
    /// can see the reason for. That is exactly the class of thing a pinned assertion is for, and it
    /// reads the shipped files rather than the parsed catalogue because <c>ImageRecipeCatalogue</c>
    /// deliberately knows only id, licence and VRAM (phase-48 deviation 3).
    /// </remarks>
    [Fact]
    public void TheThreeHundredAndSixtyRecipeSharesQwenImagesBaseExactly()
    {
        var directory = RepositoryRecipeDirectory();

        using var panorama = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "qwen-360.json")));
        using var basic = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "qwen-image.json")));

        foreach (var field in new[] { "repo", "revision", "dtype", "quantization", "pipeline" })
        {
            Assert.Equal(
                basic.RootElement.GetProperty(field).GetString(),
                panorama.RootElement.GetProperty(field).GetString());
        }

        // …and it is a different model to a client, which is the other half of D1: two recipe ids
        // over one base, never one id with a scale on it.
        Assert.Equal("equirectangular", panorama.RootElement.GetProperty("projection").GetString());
        Assert.False(basic.RootElement.TryGetProperty("projection", out _));

        var adapter = Assert.Single(panorama.RootElement.GetProperty("adapters").EnumerateArray());

        // A second repository is a second pin. Without it, "which LoRA was in 3.17.0" has no answer.
        Assert.False(string.IsNullOrWhiteSpace(adapter.GetProperty("revision").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(adapter.GetProperty("weightFile").GetString()));
    }

    private static string RepositoryRecipeDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "python", "recipes");
    }
}
