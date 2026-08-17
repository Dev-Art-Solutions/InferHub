using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Tools;
using InferHub.Shared.Images;
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

    /// <summary>
    /// Phase 58, D3. <b>A video recipe with no <c>vramMiB</c> is not declared at all</b>, and an
    /// image recipe with none still is.
    /// </summary>
    /// <remarks>
    /// Phase 48's rule — no figure means admit rather than guess — exists so a number nobody wrote
    /// down cannot refuse a model the operator can see on the box. It is right where the miss is
    /// 4-8 GB. It is a loaded gun where the same silence admits a 24 GB model onto a 12 GB card and
    /// the failure lands as a CUDA out-of-memory error four minutes into somebody's job. So the
    /// default is flipped for video <em>only</em>, which is what keeps "a deployment that changes no
    /// config behaves identically" true of every image recipe anybody already had.
    /// </remarks>
    [Fact]
    public void AVideoRecipeWithNoVramFigureIsSkippedAndAnImageRecipeWithNoneIsNot()
    {
        Write("silent-video.json", """
            {
              "id": "silent-video",
              "media": "video",
              "repo": "somebody/something",
              "revision": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
              "license": { "id": "Apache-2.0", "permissive": true }
            }
            """);

        Write("silent-image.json", """
            {
              "id": "silent-image",
              "repo": "somebody/something-else",
              "revision": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
              "license": { "id": "Apache-2.0", "permissive": true }
            }
            """);

        var catalogue = Load();

        Assert.False(catalogue.ContainsKey("silent-video"));
        Assert.True(catalogue.ContainsKey("silent-image"));

        // …and the image one is admitted by the gate exactly as it was before this phase.
        Assert.Equal(0, catalogue["silent-image"].VramMiB);
        Assert.True(VramBudget.Fits(12288, 2048, catalogue["silent-image"].VramMiB));
        Assert.False(catalogue["silent-image"].IsVideo);
    }

    /// <summary>
    /// <c>media</c> is read, and <b>absent means image</b> — which is why the eight recipes that
    /// predate video did not change by a byte when it landed (40 D1, fourth use).
    /// </summary>
    [Fact]
    public void MediaIsReadAndDefaultsToImage()
    {
        Write("sdxl.json", Sdxl);
        Write("clip.json", """
            {
              "id": "clip",
              "media": "VIDEO",
              "repo": "somebody/something",
              "revision": "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
              "license": { "id": "Apache-2.0", "permissive": true },
              "vramMiB": 16000
            }
            """);

        var catalogue = Load();

        Assert.False(catalogue["sdxl"].IsVideo);
        Assert.Equal("image", catalogue["sdxl"].Media);
        Assert.True(catalogue["clip"].IsVideo);
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
            [
                "cogvideox-2b", "flux-schnell", "qwen-360", "qwen-image", "sd15", "sd35-medium",
                "sdxl", "sdxl-turbo", "wan-t2v-1.3b", "wan-t2v-14b-720p"
            ],
            shipped.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        // The two that need a licence decision, and only those two.
        var nonPermissive = shipped.Values.Where(r => !r.Permissive).Select(r => r.Id).OrderBy(id => id).ToArray();
        Assert.Equal(["sd35-medium", "sdxl-turbo"], nonPermissive);

        // The three that only exist because they are quantized.
        Assert.Equal("nf4", shipped["flux-schnell"].Quantization);
        Assert.Equal("nf4", shipped["qwen-image"].Quantization);
        Assert.Equal("nf4", shipped["qwen-360"].Quantization);

        // The three video recipes are in the SAME catalogue and therefore behind the same two gates.
        Assert.True(shipped["wan-t2v-1.3b"].Permissive);
        Assert.Equal("none", shipped["wan-t2v-1.3b"].Quantization);
        Assert.Equal(
            ["cogvideox-2b", "wan-t2v-1.3b", "wan-t2v-14b-720p"],
            shipped.Values.Where(r => r.IsVideo).Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());

        // PHASE 58, D6 — the first recipe this project ships that does NOT fit a 24 GB card, named
        // here rather than left to arithmetic. `VramBudget.Fits` has existed since phase 48 and has
        // never withheld a shipped recipe from a target card until now; a node with 24 GB simply
        // never declares this one, so the hub never routes to it and nobody meets it at 2am.
        string[] needsMoreThanTwentyFour = ["wan-t2v-14b-720p"];

        foreach (var recipe in shipped.Values)
        {
            var fitsTwentyFour = VramBudget.Fits(24576, 2048, recipe.VramMiB);

            if (needsMoreThanTwentyFour.Contains(recipe.Id))
            {
                Assert.False(
                    fitsTwentyFour,
                    $"'{recipe.Id}' is documented as needing more than a 24 GB card. It declares "
                    + $"{recipe.VramMiB} MiB, which now fits one — update the README's table with it.");

                Assert.True(VramBudget.Fits(49152, 2048, recipe.VramMiB));
                continue;
            }

            Assert.True(
                VramBudget.Fits(24576, 2048, recipe.VramMiB),
                $"'{recipe.Id}' declares {recipe.VramMiB} MiB, which does not fit a 24 GB card with the default 2048 MiB reserve.");
        }
    }

    /// <summary>
    /// Phase 58. Every shipped video recipe declares a clock the worker requires and geometry the
    /// <em>edge</em> will accept — the two ends of a request that never meet in one file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A recipe offering a size <see cref="VideoSizes"/> refuses is a model nobody can call: the
    /// 400 arrives at the edge, naming a grid, for a size the recipe itself published. Video sizes
    /// sit on a **16** grid where every image pipeline downsamples by 8 (57 D4), so this is exactly
    /// the mistake a hand-edited recipe makes.
    /// </para>
    /// <para>
    /// <c>fps</c> and <c>durations</c> are the worker's own gate (58 D4) and this asserts the
    /// shipped files pass it, because the worker's copy runs in a container nothing in this suite
    /// starts. <c>defaults.seconds</c> must be in the list for the caller who names nothing — the
    /// one trusting the recipe.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryShippedVideoRecipeDeclaresAClockAndSizesTheEdgeAccepts()
    {
        var directory = RepositoryRecipeDirectory();
        var checkedAny = false;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var recipe = JsonDocument.Parse(File.ReadAllText(path));
            var root = recipe.RootElement;

            if (!root.TryGetProperty("media", out var media) || media.GetString() != "video")
            {
                continue;
            }

            checkedAny = true;
            var id = root.GetProperty("id").GetString();

            Assert.True(root.TryGetProperty("fps", out var fps) && fps.GetDouble() > 0, $"'{id}' declares no fps");

            var durations = root.GetProperty("durations").EnumerateArray()
                .Select(entry => (Seconds: entry.GetProperty("seconds").GetDouble(), Frames: entry.GetProperty("frames").GetInt32()))
                .ToArray();

            Assert.NotEmpty(durations);

            foreach (var (seconds, frames) in durations)
            {
                // A latent video pipeline computes (frames - 1) // 4 + 1 latent frames, so a count
                // off the 4k+1 grid is silently rounded up inside the pipeline and the clip is
                // longer than the label the response reports (57 D4, fact 3).
                Assert.True((frames - 1) % 4 == 0, $"'{id}' offers {frames} frames, which is not on the 4k+1 grid");
                Assert.True(seconds > 0);
            }

            var declaredSizes = root.GetProperty("sizes").EnumerateArray().Select(size => size.GetString()).ToArray();
            Assert.NotEmpty(declaredSizes);

            foreach (var size in declaredSizes)
            {
                Assert.True(
                    VideoSizes.TryParse(size, out _, out var error),
                    $"'{id}' offers the size '{size}', which the edge refuses: {error}");
            }

            var defaults = root.GetProperty("defaults");
            Assert.Contains(defaults.GetProperty("size").GetString(), declaredSizes);
            Assert.Contains(durations, pair => Math.Abs(pair.Seconds - defaults.GetProperty("seconds").GetDouble()) < 1e-9);
        }

        Assert.True(checkedAny, "no video recipe was found — this test would pass on an empty catalogue");
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
