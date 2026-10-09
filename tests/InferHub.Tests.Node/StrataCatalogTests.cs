using System.Collections.Concurrent;
using InferHub.Node.Backends;
using InferHub.Node.Backends.Catalog;
using InferHub.Node.Backends.HuggingFace;
using InferHub.Node.Backends.Strata;
using InferHub.Node.Profiles;
using InferHub.Shared.Contracts;
using InferHub.Shared.HuggingFace;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 99: a Strata install served as a catalogue — every <c>strata-*.json</c> config a model, the one
/// a request names served by its own <c>server.py</c>, one at a time by default; and an install from a
/// Hugging Face link run by Strata's own setup. Loaded models are real sockets
/// (<see cref="FakeColibriLauncher"/>), setup is <see cref="FakeSetupRunner"/>; the real ones are in the notes.
/// </summary>
public class StrataCatalogTests : IDisposable
{
    private readonly List<string> roots = [];

    public void Dispose()
    {
        foreach (var root in roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    // ---- D2: a model is a config ------------------------------------------------------------

    [Fact]
    public async Task EveryInstalledConfigIsAModelNamedByItsFile()
    {
        var root = Install("strata-iq2_xs", "strata-coder-iq1_m");
        File.WriteAllText(Path.Combine(root, "strata-iq2_xs.shared-settings.json"), "{}");     // chat settings, not a model
        File.WriteAllText(Path.Combine(root, "strata-mine.json"), """{"note": "the user's own"}""");
        File.WriteAllText(Path.Combine(root, "strata-cut.json"), """{"exe": "x", "ar""");      // a setup stopped half-way

        var launcher = new FakeColibriLauncher();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), launcher);
        catalog.Start();

        var models = await catalog.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["strata-coder-iq1_m", "strata-iq2_xs"], models!.Select(m => m.Name));
        Assert.Equal(["chat"], catalog.Kinds);
        Assert.Equal(["chat"], catalog.KindsFor("strata-iq2_xs"));
        Assert.Empty(launcher.Events);
        Assert.Equal("strata", catalog.Name);
    }

    [Fact]
    public async Task ARequestLaunchesItsConfigAndTheNextSizeIsASwitch()
    {
        var root = Install("strata-iq2_xs", "strata-coder-iq1_m");
        var launcher = new FakeColibriLauncher();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), launcher);
        catalog.Start();

        var first = await catalog.ChatAsync(FakeColibri.Chat("strata-iq2_xs"), CancellationToken.None);
        var second = await catalog.ChatAsync(FakeColibri.Chat("strata-coder-iq1_m"), CancellationToken.None);

        Assert.Contains("hello from strata-iq2_xs", first);
        Assert.Contains("hello from strata-coder-iq1_m", second);

        // One slot by default (a Strata model is tens of gigabytes): the first is stopped before the second loads.
        Assert.Equal(["launch:strata-iq2_xs", "stop:strata-iq2_xs", "launch:strata-coder-iq1_m"], launcher.Events);
        Assert.Equal(Path.Combine(root, "strata-iq2_xs.json"), launcher.Directories["strata-iq2_xs"]);
    }

    [Fact]
    public async Task LoadedFalseOnHealthIsNotReady()
    {
        // Strata answers /health while a lazy engine is still loading; only "loaded": true is ready.
        var launcher = new FakeColibriLauncher { LoadDelay = TimeSpan.FromMilliseconds(700) };
        var catalog = FakeStrata.Catalog(FakeStrata.Options(Install("strata-iq2_xs")), launcher);
        catalog.Start();

        var warm = catalog.WarmAsync("strata-iq2_xs", CancellationToken.None);
        await Task.Delay(250);

        Assert.False(warm.IsCompleted);
        Assert.Equal(NodeCatalogModel.Loading, catalog.State("n").Models.Single().State);

        await warm;
        Assert.Equal(NodeCatalogModel.Loaded, catalog.State("n").Models.Single().State);
    }

    [Fact]
    public async Task TheConfigsOwnApiKeyIsSentUnlessTheNodeNamesOne()
    {
        var root = Install();
        FakeStrata.WriteConfig(root, "strata-iq2_xs", apiKey: "first-key,second-key");
        FakeStrata.WriteConfig(root, "strata-q2_0");

        var keys = new ConcurrentDictionary<string, string?>();
        var launcher = new FakeColibriLauncher();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root, o => o.Serve.MaxLoaded = 2), launcher, keys);
        catalog.Start();

        await catalog.WarmAsync("strata-iq2_xs", CancellationToken.None);
        await catalog.WarmAsync("strata-q2_0", CancellationToken.None);

        Assert.Equal("first-key", keys[launcher.Alive["strata-iq2_xs"].BaseUrl]);
        Assert.Null(keys[launcher.Alive["strata-q2_0"].BaseUrl]);

        var named = new ConcurrentDictionary<string, string?>();
        var other = new FakeColibriLauncher();
        var withKey = FakeStrata.Catalog(FakeStrata.Options(root, o => o.ApiKey = "node-key"), other, named);
        withKey.Start();
        await withKey.WarmAsync("strata-iq2_xs", CancellationToken.None);

        Assert.Equal("node-key", named.Values.Single());
    }

    [Fact]
    public void AnUnknownModelIsRefusedNamingWhatIsInstalled()
    {
        var catalog = FakeStrata.Catalog(FakeStrata.Options(Install("strata-iq2_xs")), new FakeColibriLauncher());
        catalog.Start();

        var refused = Assert.ThrowsAsync<CatalogException>(() => catalog.ChatAsync(FakeColibri.Chat("strata-iq3_s"), CancellationToken.None)).Result;

        Assert.True(refused.NotInCatalogue);
        Assert.Contains("strata on this node has no model 'strata-iq3_s'; it has strata-iq2_xs", refused.Message);
    }

    [Fact]
    public void PullAndDeleteAreSentencesNotSurprises()
    {
        var catalog = FakeStrata.Catalog(FakeStrata.Options(Install("strata-iq2_xs")), new FakeColibriLauncher());

        Assert.False(catalog.SupportsPull);
        Assert.Contains("Hugging Face link", Assert.Throws<NotSupportedException>(() => catalog.PullAsync("x", CancellationToken.None)).Message);
        Assert.Contains("share files", Assert.ThrowsAsync<NotSupportedException>(() => catalog.DeleteAsync("strata-iq2_xs", CancellationToken.None)).Result.Message);
    }

    // ---- the hub's selection -----------------------------------------------------------------

    [Fact]
    public async Task APinFromTheProfileLoadsAndANewPinSwitches()
    {
        var launcher = new FakeColibriLauncher();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(Install("strata-iq2_xs", "strata-coder-iq1_m")), launcher);
        catalog.Start();

        var changes = await catalog.ApplyAsync(new CatalogProfile(["strata-coder-iq1_m"]), CancellationToken.None);
        Assert.Equal(["strata 'strata-coder-iq1_m' pinned"], changes);
        await Until(() => catalog.State("n").Models.Any(m => m is { Name: "strata-coder-iq1_m", State: NodeCatalogModel.Loaded }));

        await catalog.ApplyAsync(new CatalogProfile(["strata-iq2_xs"]), CancellationToken.None);
        await Until(() => catalog.State("n").Models.Any(m => m is { Name: "strata-iq2_xs", State: NodeCatalogModel.Loaded }));

        Assert.Equal(["launch:strata-coder-iq1_m", "stop:strata-coder-iq1_m", "launch:strata-iq2_xs"], launcher.Events);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UnloadAsync("strata-iq2_xs", CancellationToken.None));
        Assert.Contains("strata.loaded", refused.Message);
    }

    [Fact]
    public void TheClampPicksFromTheStrataCatalogueOnly()
    {
        var local = new LocalCeiling([], false, [], null, true, StrataCatalogue: ["strata-iq2_xs"], StrataMaxLoaded: 1);
        var profile = new NodeProfile("p", 1, new NodeProfileSelector(), Strata: new CatalogProfile(["strata-iq2_xs", "strata-iq3_s"], OnDemand: true));

        var result = NodeProfileClamp.Apply(local, profile);

        Assert.Equal(["strata-iq2_xs"], result.Effective.Strata!.Loaded!);
        Assert.True(result.Effective.Strata.OnDemand);
        Assert.Contains(result.Refusals, r => r.Item == "strata:strata-iq3_s" && r.Reason.Contains("has no 'strata-iq3_s'"));

        var none = NodeProfileClamp.Apply(new LocalCeiling([], false, [], null, true), profile);
        Assert.Null(none.Effective.Strata);
        Assert.Contains(none.Refusals, r => r.Item == "strata" && r.Reason.Contains("no strata catalogue"));
    }

    [Fact]
    public void TheStateOffersStrataSizesOnlyWhenTheNodeInstalls()
    {
        var catalog = FakeStrata.Catalog(FakeStrata.Options(Install("strata-coder-iq1_m")), new FakeColibriLauncher());

        Assert.Null(catalog.State("n").Installable);

        catalog.InstallsFromHub = true;
        var installable = catalog.State("n").Installable!;

        Assert.Equal(9, installable.Count);
        var coder = installable.Single(i => i.Name == "strata-coder-iq1_m");
        Assert.True(coder.Installed);
        Assert.Equal("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF:IQ1_M", coder.Link);
        Assert.False(installable.Single(i => i.Name == "strata-iq2_xs").Installed);
    }

    // ---- the command lines --------------------------------------------------------------------

    [Fact]
    public void TheServerIsStrataOwnOnLoopbackWithNoContentTee()
    {
        var root = Install("strata-iq2_xs");
        var options = FakeStrata.Options(root, o =>
        {
            o.Python = "/usr/bin/python3";
            o.Serve.Gpu = "1";
            o.Serve.Arguments = ["--fit-max-tokens"];
            o.ApiKey = "node-key";
        });

        Environment.SetEnvironmentVariable(StrataServe.ContentTeeVariable, "1");

        try
        {
            var info = StrataServe.StartInfo(options, Path.Combine(root, "strata-iq2_xs.json"), 18301);

            Assert.Equal("/usr/bin/python3", info.FileName);
            Assert.Equal(root, info.WorkingDirectory);
            Assert.Equal(
                [Path.Combine(root, "serve", "server.py"), "--engine", "strata", "--config", Path.Combine(root, "strata-iq2_xs.json"), "--host", "127.0.0.1", "--port", "18301", "--gpu", "1", "--fit-max-tokens"],
                info.ArgumentList);
            Assert.False(info.Environment.ContainsKey(StrataServe.ContentTeeVariable));
            Assert.Equal("node-key", info.Environment[StrataServe.ApiKeyVariable]);
            Assert.DoesNotContain("node-key", info.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StrataServe.ContentTeeVariable, null);
        }
    }

    [Fact]
    public void TheInstallIsSetupsOwnWithTheNodesConsentAndToken()
    {
        var root = Install();
        var options = FakeStrata.Options(root, o =>
        {
            o.Python = "py";
            o.DataDir = "/data/strata";
            o.Install.Context = 65536;
            o.Install.Arguments = ["--kv", "q4_0"];
        });
        var hf = new HuggingFaceOptions { Enabled = true, Token = "hf_secret", Endpoint = "https://hf-mirror.com" };

        var info = StrataInstaller.StartInfo(options, hf, "coder", "IQ1_M");

        Assert.Equal(
            [Path.Combine(root, "setup.py"), "--setup", "--yes", "--no-start", "--no-browser", "--family", "coder", "--model", "IQ1_M",
             "--host", "127.0.0.1", "--vision", "no", "--data-dir", "/data/strata", "--context", "65536", "--kv", "q4_0"],
            info.ArgumentList);
        Assert.Equal("hf_secret", info.Environment["HF_TOKEN"]);
        Assert.Equal("https://hf-mirror.com", info.Environment["HF_ENDPOINT"]);
        Assert.DoesNotContain("hf_secret", info.ArgumentList);
    }

    [Fact]
    public void FlagsTheNodeOwnsAreRefusedAtStartup()
    {
        var options = FakeStrata.Options(Install(), o =>
        {
            o.Serve.Arguments = ["--api-monitor", "--port=9000"];
            o.Install.Arguments = ["--model", "IQ3_S"];
            o.Serve.Engine = "vllm";
        });
        var validator = new StrataOptionsValidator(Options.Create(new BackendOptions { Type = BackendOptions.Strata }));

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("'--api-monitor'") && f.Contains("rule 7"));
        Assert.Contains(result.Failures, f => f.Contains("'--port'"));
        Assert.Contains(result.Failures, f => f.Contains("Install:Arguments has '--model'"));
        Assert.Contains(result.Failures, f => f.Contains("'strata' or 'mock'"));

        // Inert on a node that does not run Strata.
        Assert.True(new StrataOptionsValidator(Options.Create(new BackendOptions())).Validate(null, options).Succeeded);
    }

    // ---- phase 100: pictures ----------------------------------------------------------------------

    [Fact]
    public void EachModelSaysWhetherItReadsPictures()
    {
        var root = Install();
        FakeStrata.WriteConfig(root, "strata-coder-iq1_m", images: true);
        FakeStrata.WriteConfig(root, "strata-q2_0");

        var models = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher()).State("n").Models;

        Assert.True(models.Single(m => m.Name == "strata-coder-iq1_m").Images);
        Assert.False(models.Single(m => m.Name == "strata-q2_0").Images);
    }

    [Fact]
    public void AnEncoderIsTheVisionBlockOrTheVisionArgument()
    {
        var root = Install();
        File.WriteAllText(Path.Combine(root, "a.json"), """{"exe": "s", "args": ["--vision"]}""");
        File.WriteAllText(Path.Combine(root, "b.json"), """{"exe": "s", "args": [], "vision": {"gpu": false}}""");
        File.WriteAllText(Path.Combine(root, "c.json"), """{"exe": "s", "args": ["--vision-tokens", "300"], "vision": null}""");
        File.WriteAllText(Path.Combine(root, "d.json"), """{"exe": "s", "ar""");

        Assert.True(StrataCatalog.HasImageEncoder(Path.Combine(root, "a.json")));
        Assert.True(StrataCatalog.HasImageEncoder(Path.Combine(root, "b.json")));
        Assert.False(StrataCatalog.HasImageEncoder(Path.Combine(root, "c.json")));
        Assert.Null(StrataCatalog.HasImageEncoder(Path.Combine(root, "d.json")));
        Assert.Null(StrataCatalog.HasImageEncoder(Path.Combine(root, "missing.json")));
    }

    [Theory]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":"what is in it?","images":["iVBORw0KGgo="]}]}""", true)]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"x"},{"type":"image_url","image_url":{"url":"data:image/png;base64,iVBO"}}]}]}""", true)]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":"draw me an image","images":[]}]}""", false)]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":"an image_url is a link"}]}""", false)]
    [InlineData("""{"model":"m","messages":"image"}""", false)]
    public void APictureIsAnImagesEntryOrAnImagePart(string request, bool pictures)
        => Assert.Equal(pictures, StrataCatalog.HasPictures(request));

    [Fact]
    public async Task APictureForASizeWithoutTheEncoderIsRefusedBeforeItsServerStarts()
    {
        var root = Install();
        FakeStrata.WriteConfig(root, "strata-q2_0");
        FakeStrata.WriteConfig(root, "strata-coder-iq1_m", images: true);
        var launcher = new FakeColibriLauncher();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), launcher);
        catalog.Start();

        var refused = await Assert.ThrowsAsync<CatalogException>(() => catalog.ChatAsync(Picture("strata-q2_0"), CancellationToken.None));
        Assert.Contains("'strata-q2_0' was set up without Strata's image encoder", refused.Message);
        Assert.Contains("\"vision\": \"yes\"", refused.Message);
        Assert.DoesNotContain("iVBORw0KGgo", refused.Message);

        var streamed = await Assert.ThrowsAsync<CatalogException>(async () =>
        {
            await foreach (var _ in catalog.StreamAsync("chat", Picture("strata-q2_0"), CancellationToken.None))
            {
            }
        });
        Assert.Equal(refused.Message, streamed.Message);

        // Nothing was started, and nothing evicted; the same size answers a chat without pictures.
        Assert.Empty(launcher.Events);
        Assert.Contains("hello from strata-q2_0", await catalog.ChatAsync(FakeColibri.Chat("strata-q2_0"), CancellationToken.None));

        // A size set up with the encoder is sent the picture.
        Assert.Contains("hello from strata-coder-iq1_m", await catalog.ChatAsync(Picture("strata-coder-iq1_m"), CancellationToken.None));
        Assert.Equal(["launch:strata-q2_0", "stop:strata-q2_0", "launch:strata-coder-iq1_m"], launcher.Events);
    }

    [Fact]
    public void TheHubsVisionIsSetupsAndTheNodesIsTheDefault()
    {
        var root = Install();
        var options = FakeStrata.Options(root, o => o.Install.Vision = "cpu");
        var hf = new HuggingFaceOptions { Enabled = true };

        Assert.Equal("cpu", Arg(StrataInstaller.StartInfo(options, hf, "coder", "IQ1_M"), "--vision"));
        Assert.Equal("yes", Arg(StrataInstaller.StartInfo(options, hf, "coder", "IQ1_M", "yes"), "--vision"));
    }

    [Fact]
    public async Task PicturesAskedForAnInstalledSizeRunSetupAgainAndTheConfigGainsTheEncoder()
    {
        var root = Install("strata-coder-iq1_m");
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        catalog.Start();
        var runner = new FakeSetupRunner(root);
        var installer = FakeStrata.Installer(catalog, FakeStrata.Options(root), runner);

        // A plain install of an installed size is still nothing to do; so is "no".
        Assert.Equal(["'strata-coder-iq1_m' is already installed"], (await Collect(installer.InstallAsync(Link(CoderLink), CancellationToken.None))).Select(f => f.Status));
        Assert.Equal(["'strata-coder-iq1_m' is already installed"], (await Collect(installer.InstallAsync(Link(CoderLink), "no", CancellationToken.None))).Select(f => f.Status));
        Assert.Empty(runner.Runs);

        var frames = await Collect(installer.InstallAsync(Link(CoderLink), "yes", CancellationToken.None));

        Assert.Equal("adding pictures to Qwen3.8-Flash-Next Coder IQ1_M with Strata's setup", frames[0].Status);
        Assert.Equal("installed as 'strata-coder-iq1_m', with pictures", frames[^1].Status);
        var encoder = frames.Last(f => f.Status == "downloading vision encoder");
        Assert.Equal(910_000_000, encoder.Total);
        Assert.Equal(910_000_000, encoder.Completed);
        Assert.Equal("yes", Arg(runner.Runs.Single(), "--vision"));
        Assert.True(catalog.State("n").Models.Single().Images);

        // Setup answers every question again for a size; the config's own context and KV go back in.
        Assert.Equal("32768", Arg(runner.Runs.Single(), "--context"));
        Assert.Equal("int8", Arg(runner.Runs.Single(), "--kv"));

        // Asked again: there is nothing left to add.
        Assert.Equal(["'strata-coder-iq1_m' is already installed, with pictures"], (await Collect(installer.InstallAsync(Link(CoderLink), "yes", CancellationToken.None))).Select(f => f.Status));
        Assert.Single(runner.Runs);
    }

    [Fact]
    public void TheNodesOwnInstallChoicesWinOverTheKeptOnes()
    {
        var root = Install("strata-coder-iq1_m");
        var config = Path.Combine(root, "strata-coder-iq1_m.json");

        Assert.Equal(["--context", "32768", "--kv", "int8"], StrataInstaller.KeptChoices(FakeStrata.Options(root), config));
        Assert.Equal(["--kv", "int8"], StrataInstaller.KeptChoices(FakeStrata.Options(root, o => o.Install.Context = 65536), config));
        Assert.Equal(["--context", "32768"], StrataInstaller.KeptChoices(FakeStrata.Options(root, o => o.Install.Arguments = ["--kv=q4_0"]), config));
        Assert.Empty(StrataInstaller.KeptChoices(FakeStrata.Options(root), Path.Combine(root, "missing.json")));

        // A fresh install keeps nothing: there is no config to keep from.
        Assert.Null(Arg(StrataInstaller.StartInfo(FakeStrata.Options(root), new HuggingFaceOptions(), "qwen", "Q2_0"), "--context"));
    }

    [Fact]
    public async Task PicturesAreNotAddedToALoadedSize()
    {
        var root = Install("strata-coder-iq1_m");
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        catalog.Start();
        await catalog.WarmAsync("strata-coder-iq1_m", CancellationToken.None);
        var runner = new FakeSetupRunner(root);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(FakeStrata.Installer(catalog, FakeStrata.Options(root), runner).InstallAsync(Link(CoderLink), "yes", CancellationToken.None)));

        Assert.Contains("'strata-coder-iq1_m' is loaded; unload it", refused.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task ASetupThatLeavesOutTheEncoderIsAFailureNotASuccess()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var installer = FakeStrata.Installer(catalog, FakeStrata.Options(root), new FakeSetupRunner(root) { DropVision = true });

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(installer.InstallAsync(Link(CoderLink), "cpu", CancellationToken.None)));

        Assert.Contains("has no image encoder (--vision cpu was asked for)", failed.Message);
    }

    [Fact]
    public async Task AVisionSetupDoesNotKnowIsRefusedBeforeItRuns()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var runner = new FakeSetupRunner(root);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Collect(FakeStrata.Installer(catalog, FakeStrata.Options(root), runner).InstallAsync(Link(CoderLink), "--build", CancellationToken.None)));

        Assert.Contains("not setup.py's --vision", refused.Message);
        Assert.Empty(runner.Runs);
    }

    // ---- D4: an install from a Hugging Face link ---------------------------------------------------

    [Fact]
    public async Task AnInstallRelaysSetupsDownloadAndListsTheNewConfig()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        catalog.Start();
        var changed = 0;
        catalog.Changed += () => Interlocked.Increment(ref changed);
        var runner = new FakeSetupRunner(root);

        var frames = await Collect(FakeStrata.Installer(catalog, FakeStrata.Options(root), runner).InstallAsync(Link("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF"), CancellationToken.None));

        Assert.Equal("coder", runner.Runs.Single().ArgumentList[6]);
        Assert.Contains(frames, f => f.Status == "step 5: downloading Qwen3.8-Flash-Next IQ1_M");

        // The bar redraws within a percent; each percent is one frame, with bytes.
        var downloads = frames.Where(f => f.Status.StartsWith("downloading ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, downloads.Length);
        Assert.Equal(29_610_000_000, downloads[^1].Total);
        Assert.Equal(29_610_000_000, downloads[^1].Completed);

        Assert.Equal("installed as 'strata-coder-iq1_m'", frames[^1].Status);
        Assert.Contains("strata-coder-iq1_m", catalog.CatalogNames);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task AnInstalledSizeIsNotInstalledTwice()
    {
        var root = Install("strata-iq2_xs");
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var runner = new FakeSetupRunner(root);

        var frames = await Collect(FakeStrata.Installer(catalog, FakeStrata.Options(root), runner).InstallAsync(Link("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF:IQ2_XS"), CancellationToken.None));

        Assert.Equal(["'strata-iq2_xs' is already installed"], frames.Select(f => f.Status));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task AFailedSetupSaysWhatSetupSaid()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var installer = FakeStrata.Installer(catalog, FakeStrata.Options(root), new FakeSetupRunner(root) { ExitCode = 1 });

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(installer.InstallAsync(Link("unsloth/Qwen3.8-Flash-Next-GGUF:UD-IQ4_XS"), CancellationToken.None)));

        Assert.Contains("exited with code 1", failed.Message);
        Assert.Contains("not enough free disk space", failed.Message);
        Assert.Empty(catalog.CatalogNames);
    }

    [Fact]
    public async Task ASetupThatWritesNoConfigIsAFailureNotASuccess()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var installer = FakeStrata.Installer(catalog, FakeStrata.Options(root), new FakeSetupRunner(root) { WriteNothing = true });

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(installer.InstallAsync(Link("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF:Q2_0"), CancellationToken.None)));

        Assert.Contains("no strata-q2_0.json", failed.Message);
    }

    [Fact]
    public async Task ASizeStrataDoesNotHaveIsRefusedBeforeSetupRuns()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var runner = new FakeSetupRunner(root);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Collect(FakeStrata.Installer(catalog, FakeStrata.Options(root), runner).InstallAsync(Link("ukisai/Swift-1.5-Qwen3.8-Flash-Next-GSQ-RCO-GGUF:IQ3_S"), CancellationToken.None)));

        Assert.Contains("IQ2_XS, IQ3_XXS", refused.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task ASecondInstallWaitsForTheFirstAndSaysSo()
    {
        var root = Install();
        var catalog = FakeStrata.Catalog(FakeStrata.Options(root), new FakeColibriLauncher());
        var runner = new FakeSetupRunner(root) { Gate = new TaskCompletionSource() };
        var installer = FakeStrata.Installer(catalog, FakeStrata.Options(root), runner);

        var first = Collect(installer.InstallAsync(Link("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF:Q2_0"), CancellationToken.None));
        await Until(() => !runner.Runs.IsEmpty);

        var second = Collect(installer.InstallAsync(Link("ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF"), CancellationToken.None));
        await Task.Delay(200);
        Assert.Single(runner.Runs);

        runner.Gate.SetResult();
        await first;
        var frames = await second;

        Assert.Equal("waiting: another Strata install is running on this node", frames[0].Status);
        Assert.Equal(["strata-coder-iq1_m", "strata-q2_0"], catalog.CatalogNames);
    }

    // ------------------------------------------------------------------------------------------------

    private const string CoderLink = "ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF:IQ1_M";

    /// <summary>A chat with a picture in it, as the node receives one (phase 64's <c>images</c>).</summary>
    private static string Picture(string model) =>
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":"what is in it?","images":["iVBORw0KGgo="]}],"stream":false}""";

    private static string? Arg(System.Diagnostics.ProcessStartInfo info, string flag)
    {
        var i = info.ArgumentList.IndexOf(flag);
        return i >= 0 && i + 1 < info.ArgumentList.Count ? info.ArgumentList[i + 1] : null;
    }

    private string Install(params string[] configs)
    {
        var root = FakeStrata.Install(configs);
        roots.Add(root);
        return root;
    }

    private static HfReference Link(string link)
    {
        Assert.True(HfReference.TryParse(link, null, out var reference, out var error), error);
        return reference!;
    }

    private static async Task<List<ModelPullProgress>> Collect(IAsyncEnumerable<ModelPullProgress> frames)
    {
        var list = new List<ModelPullProgress>();

        await foreach (var frame in frames)
        {
            list.Add(frame);
        }

        return list;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(50);
        }
    }
}
