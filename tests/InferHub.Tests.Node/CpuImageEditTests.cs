using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// Phase 92. <c>lcm-dreamshaper</c> edits and varies a picture on a CPU — without a mask, because
/// there is no LCM inpainting pipeline — and an LCM edit is billed for the steps it actually runs.
/// </summary>
/// <remarks>
/// <b>These run the shipped <c>diffusion_worker.py</c></b>, imported with <c>importlib</c> under an
/// interpreter with numpy and Pillow (<see cref="PythonNumpyFactAttribute"/>), the way
/// <see cref="NegativePromptTests"/> does. CI has no torch, so <c>edit()</c> and <c>run_batch()</c>
/// are driven with a stand-in <c>torch</c> module and a fake pipeline that fires the progress
/// callback a chosen number of times — which is the one thing about a pipeline these tests are
/// about. The real step counts (4 at every strength on an LCM) were measured on the pinned wheel
/// and are recorded in the recipe's notes.
/// </remarks>
public class CpuImageEditTests
{
    private const string Prelude = """
        import importlib.util, json, os, sys, tempfile, types

        os.environ["INFERHUB_IMAGE_RECIPES"] = sys.argv[2]
        spec = importlib.util.spec_from_file_location("diffusion_worker", sys.argv[1])
        dw = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(dw)

        from PIL import Image
        from inferhub_worker import File

        recipes = dw.load_recipes()
        lcm = recipes["lcm-dreamshaper"]

        # A stand-in for the two things run_batch takes from torch: a seeded CPU generator and a seed.
        fake_torch = types.ModuleType("torch")
        class Generator:
            def __init__(self, device="cpu"): pass
            def manual_seed(self, seed): return self
        fake_torch.Generator = Generator
        fake_torch.seed = lambda: 1234
        sys.modules["torch"] = fake_torch

        # Fires the progress callback `runs` times, as a diffusers pipeline does once per step.
        class Pipe:
            def __init__(self, runs): self.runs, self.calls = runs, []
            def __call__(self, **kwargs):
                self.calls.append(kwargs)
                callback = kwargs.get("callback_on_step_end")
                for step in range(self.runs):
                    if callback: callback(self, step, 999 - step, {})
                return types.SimpleNamespace(images=[kwargs["image"].copy()])

        logs = []
        dw.log = logs.append
        dw.accepted_licenses = lambda: set()

        def request(model, payload, mask=False):
            scratch = tempfile.mkdtemp()
            files = []
            for role, mode, colour in (("image", "RGB", (200, 80, 40)), ("mask", "RGBA", (0, 0, 0, 0))):
                if role == "mask" and not mask: continue
                path = os.path.join(scratch, role + ".png")
                Image.new(mode, (512, 512), colour).save(path)
                files.append(File(role, "image/png", path))
            r = dw.Request(id="r1", capability="image-edit", model=model, payload=payload, files=files, scratch=scratch)
            frames = []
            r.progress = lambda step, total_steps=None: frames.append((step, total_steps))
            return r, frames

        def refused(fn, *args):
            try:
                fn(*args)
            except dw.ToolError as error:
                return error
            raise AssertionError("expected a refusal")

        def must_not_load(recipe, accepted):
            raise AssertionError(f"'{recipe['id']}' was loaded for a request that should have been refused")

        """;

    [PythonNumpyFact]
    public void TheSmallRecipeIsDeclaredForEditingAndOnlyItSaysItCannotInpaint() => Run("""
        assert dw.operations_of(lcm) == ["generate", "edit", "variation"], dw.operations_of(lcm)
        assert lcm["defaults"]["strength"] == 0.75, lcm["defaults"]

        frames = {f["kind"]: f["models"] for f in dw.capability_frames(recipes, dw.offered(recipes, "cpu"))}
        assert "lcm-dreamshaper" in frames["image-edit"] and "sd15" in frames["image-edit"], frames
        assert "lcm-dreamshaper" in frames["image"], frames

        assert [i for i, r in recipes.items() if r.get("inpaint") is False] == ["lcm-dreamshaper"]
        assert [i for i, r in recipes.items() if r.get("strengthKeepsSteps") is True] == ["lcm-dreamshaper"]
        """);

    /// <summary>
    /// The load-bearing one. An SD pipeline skips to <c>int(steps × strength)</c>; an LCM runs all
    /// of its steps from a later point. Billing it the SD way charged half the work at 0.5.
    /// </summary>
    [PythonNumpyFact]
    public void AnLcmEditIsBilledForEveryStepItRunsAndAnSdEditForTheOnesItSkipsTo() => Run("""
        for strength in (0.25, 0.5, 0.75, 1.0):
            assert dw.edit_steps(lcm, 4, strength) == 4, strength
        assert dw.edit_steps(recipes["sd15"], 30, 0.75) == 22
        assert dw.edit_steps(recipes["sd15"], 4, 0.1) == 1, "never zero steps"

        fake = Pipe(runs=4)
        dw.load = lambda recipe, accepted: (object(), 0.0, [])
        dw.derived_pipeline = lambda pipe, recipe, inpaint: fake

        r, frames = request("lcm-dreamshaper", {"prompt": "the same lighthouse in winter", "strength": 0.5})
        payload, files = dw.edit(r, dw.EDIT)

        assert payload["steps"] == 4 and payload["images"][0]["steps"] == 4, payload
        assert payload["strength"] == 0.5 and payload["masked"] is False, payload
        assert frames == [(1, 4), (2, 4), (3, 4), (4, 4)], frames
        assert fake.calls[0]["num_inference_steps"] == 4 and fake.calls[0]["strength"] == 0.5
        assert "mask_image" not in fake.calls[0] and "width" not in fake.calls[0]
        assert not [l for l in logs if "MISMATCH" in l], logs

        # A variation is the same pipeline with no words, at the recipe's default strength.
        r, frames = request("lcm-dreamshaper", {})
        payload, _ = dw.edit(r, dw.VARIATION)
        assert payload["steps"] == 4 and payload["strength"] == 0.75 and fake.calls[-1]["prompt"] == "", payload
        """);

    /// <summary>
    /// The next recipe whose step arithmetic nobody measured says so in the node's log, instead of
    /// being billed wrong in silence for twelve releases.
    /// </summary>
    [PythonNumpyFact]
    public void APipelineThatRunsADifferentNumberOfStepsThanWasBilledIsLoggedByName() => Run("""
        dw.load = lambda recipe, accepted: (object(), 0.0, [])
        dw.derived_pipeline = lambda pipe, recipe, inpaint: Pipe(runs=4)

        # sd15 at 4 steps and strength 0.5 bills 2; this pipeline runs 4.
        r, _ = request("sd15", {"prompt": "p", "strength": 0.5, "steps": 4})
        payload, _ = dw.edit(r, dw.EDIT)
        assert payload["steps"] == 2, payload
        mismatch = [l for l in logs if "STEP COUNT MISMATCH" in l]
        assert len(mismatch) == 1 and "ran 4 steps and 2 were reported" in mismatch[0], logs
        """);

    [PythonNumpyFact]
    public void AMaskedEditOnTheSmallRecipeIsRefusedByNameBeforeTheModelLoads() => Run("""
        dw.load = must_not_load

        r, _ = request("lcm-dreamshaper", {"prompt": "p", "has_mask": True}, mask=True)
        error = refused(dw.edit, r, dw.EDIT)
        assert error.code == dw.ERROR_INVALID_REQUEST, error.code
        assert "'lcm-dreamshaper' edits without a mask only" in str(error), str(error)
        assert "LatentConsistencyModelPipeline" in str(error) and "sd15" in str(error), str(error)

        # sd15 inpaints: the same request gets past the check and as far as the load.
        r, _ = request("sd15", {"prompt": "p", "has_mask": True}, mask=True)
        try:
            dw.edit(r, dw.EDIT)
            raise SystemExit("sd15 never reached the load")
        except AssertionError as error:
            assert "'sd15' was loaded" in str(error), str(error)
        """);

    private static void Run(string body)
    {
        var python = PythonNumpyTestGate.Interpreter!;
        var root = RepositoryRoot();
        var script = Path.Combine(Path.GetTempPath(), $"inferhub-cpu-edit-{Guid.NewGuid():N}.py");

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

            Assert.True(
                process.ExitCode == 0 && stdout.Result.Contains("ok"),
                $"exit {process.ExitCode}\n{stdout.Result}\n{stderr.Result}");
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
