using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// Phase 121 in the shipped <c>diffusion_worker.py</c>: it reads the shipped recipes and the ones the node wrote, a recipe
/// written after it started is weighed and declared on the next re-declaration (a pull's), and a recipe the node wrote
/// loads safetensors only. Run the way <see cref="NegativePromptTests"/> runs the worker's functions.
/// </summary>
public class RecipeWorkerTests
{
    private const string Prelude = """
        import importlib.util, json, os, sys, tempfile

        added = tempfile.mkdtemp()
        os.environ["INFERHUB_IMAGE_RECIPES"] = sys.argv[2] + os.pathsep + added
        spec = importlib.util.spec_from_file_location("diffusion_worker", sys.argv[1])
        dw = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(dw)

        def write(recipe):
            with open(os.path.join(added, recipe["id"] + ".json"), "w", encoding="utf-8") as handle:
                json.dump(recipe, handle)

        derived = {
            "id": "dream-8", "derivedFrom": "sd15", "repo": "someone/Dream-8", "revision": "0123456789abcdef",
            "license": {"id": "CreativeML-OpenRAIL-M", "permissive": True}, "variant": "fp16", "useSafetensors": True,
            "vramMiB": 4000, "pipeline": "StableDiffusionPipeline", "dtype": "float16", "quantization": "none",
            "operations": ["generate"], "sizes": ["512x512"], "defaults": {"steps": 30, "size": "512x512"}, "cpuViable": True,
        }

        """;

    [PythonNumpyFact]
    public void TheShippedRecipesAndTheAddedOnesAreReadAndTheShippedOneWinsAClash() => Run("""
        write(derived)
        write({**derived, "id": "sd15", "repo": "someone/else"})
        recipes = dw.load_recipes()
        assert "dream-8" in recipes and "sdxl" in recipes, sorted(recipes)
        assert recipes["sd15"]["repo"] != "someone/else", recipes["sd15"]["repo"]

        # An empty first entry is the image's own default, as an unset variable always was.
        os.environ["INFERHUB_IMAGE_RECIPES"] = os.pathsep + added
        assert dw.recipes_directories() == [dw.DEFAULT_RECIPES, added], dw.recipes_directories()
        os.environ["INFERHUB_IMAGE_RECIPES"] = ""
        assert dw.recipes_directories() == [dw.DEFAULT_RECIPES]
        """);

    [PythonNumpyFact]
    public void ARecipeWrittenAfterStartupIsDeclaredOnTheNextRedeclaration() => Run("""
        class Fake:
            frames = None
            def redeclare(self, frames, fetching=None):
                Fake.frames = frames

        dw._worker = Fake()
        dw._device = "cuda"
        dw._weighed = True
        dw._offerable = dw.offered(dw.load_recipes(), "cuda")
        dw.is_ready = lambda recipe: True
        dw.can_encode_video = lambda: True
        assert "dream-8" not in dw._offerable

        write(derived)
        dw.redeclare()
        image = next(f for f in Fake.frames if f["kind"] == "image")
        assert "dream-8" in image["models"], image

        # A worker that never weighed a catalogue (it started with none) does not guess a device for it.
        dw._weighed = False
        dw._offerable = []
        dw.redeclare()
        assert "dream-8" not in next(f for f in Fake.frames if f["kind"] == "image")["models"]
        """);

    [PythonNumpyFact]
    public void ARecipeTheNodeWroteLoadsSafetensorsOnlyAndAShippedOneIsUnchanged() => Run("""
        calls = []

        class Pipe:
            @classmethod
            def from_pretrained(cls, repo, **kwargs):
                calls.append(kwargs)
                return cls()

        dw.pipeline_class = lambda name: Pipe
        dw.dtype_of = lambda recipe: "float16"
        dw.apply_adapters = lambda pipe, recipe, local: []

        dw._from_pretrained(derived, local_files_only=True)
        assert calls[-1].get("use_safetensors") is True and calls[-1]["variant"] == "fp16", calls[-1]

        dw._from_pretrained(dw.load_recipes()["sd15"], local_files_only=True)
        assert "use_safetensors" not in calls[-1], calls[-1]
        """);

    private static void Run(string body)
    {
        var python = PythonNumpyTestGate.Interpreter!;
        var root = RepositoryRoot();
        var script = Path.Combine(Path.GetTempPath(), $"inferhub-recipe-worker-{Guid.NewGuid():N}.py");

        File.WriteAllText(script, Prelude + body + "\nprint(\"ok\")\n");

        try
        {
            var start = new ProcessStartInfo(python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            start.ArgumentList.Add(script);
            start.ArgumentList.Add(Path.Combine(root, "python", "tools", "diffusion_worker.py"));
            start.ArgumentList.Add(Path.Combine(root, "python", "recipes"));
            start.Environment["PYTHONIOENCODING"] = "utf-8";

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            Assert.True(process.WaitForExit(120_000), "the worker's functions did not finish in two minutes");
            Assert.True(process.ExitCode == 0 && stdout.Result.Contains("ok"), $"exit {process.ExitCode}\n{stdout.Result}\n{stderr.Result}");
        }
        finally
        {
            File.Delete(script);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
