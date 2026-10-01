using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// Phase 91. A negative prompt a model cannot honour is refused by name, never dropped — and the
/// small CPU recipe that made the question real is offered on a CPU-only box.
/// </summary>
/// <remarks>
/// <b>These run the shipped <c>diffusion_worker.py</c></b>, imported with <c>importlib</c> under an
/// interpreter with numpy and Pillow (<see cref="PythonNumpyFactAttribute"/>), the way
/// <c>CubemapReprojectionTests</c> does. Before this phase the worker's retry popped
/// <c>negative_prompt</c> together with the progress callback whenever a pipeline rejected either,
/// so a caller who said "no text in the picture" to a model that could not hear it got a 200 and
/// whatever the model drew. The echo worker cannot reach that path; only the real worker's
/// functions can.
/// </remarks>
public class NegativePromptTests
{
    private const string Prelude = """
        import importlib.util, json, os, sys

        os.environ["INFERHUB_IMAGE_RECIPES"] = sys.argv[2]
        spec = importlib.util.spec_from_file_location("diffusion_worker", sys.argv[1])
        dw = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(dw)

        recipes = dw.load_recipes()
        lcm = recipes["lcm-dreamshaper"]

        def refused(fn, *args):
            try:
                fn(*args)
            except dw.ToolError as error:
                return error
            raise AssertionError("expected a refusal")

        """;

    [PythonNumpyFact]
    public void ARecipeWithNoNegativePromptRefusesOneByNameBeforeAnythingLoads() => Run("""
        for identifier in ("lcm-dreamshaper", "flux-schnell"):
            error = refused(dw.resolve_negative, recipes[identifier], {"negative_prompt": "text"}, 8.0)
            assert error.code == dw.ERROR_INVALID_REQUEST, error.code
            assert f"'{identifier}' cannot take a negative_prompt" in str(error), str(error)
            assert recipes[identifier]["pipeline"] in str(error), str(error)

        # The flag is on exactly the two models whose pipeline ignores the argument outright.
        assert sorted(i for i, r in recipes.items() if r.get("negativePrompt") is False) == ["flux-schnell", "lcm-dreamshaper"]

        # Absent or empty is not a negative prompt, whatever the guidance.
        assert dw.resolve_negative(lcm, {"prompt": "a lighthouse"}, 8.0) is None
        assert dw.resolve_negative(lcm, {"negative_prompt": ""}, 8.0) is None
        """);

    /// <summary>
    /// Every other pipeline in the catalogue runs its unconditional pass only when guidance is
    /// above 1 — so <c>sdxl-turbo</c> at its default 0.0 ignored a negative prompt silently.
    /// </summary>
    [PythonNumpyFact]
    public void ANegativePromptWithGuidanceAtOrBelowOneIsRefusedBecauseItWouldBeIgnored() => Run("""
        turbo = recipes["sdxl-turbo"]
        error = refused(dw.resolve_negative, turbo, {"negative_prompt": "text"}, float(turbo["defaults"]["guidance"]))
        assert "only acts when guidance is above 1" in str(error) and "'sdxl-turbo' defaults to 0" in str(error), str(error)
        refused(dw.resolve_negative, recipes["sd15"], {"negative_prompt": "text"}, 1.0)

        assert dw.resolve_negative(turbo, {"negative_prompt": "text"}, 2.0) == "text"
        assert dw.resolve_negative(recipes["sd15"], {"negative_prompt": "text"}, 7.5) == "text"
        assert dw.resolve_negative(recipes["qwen-image"], {"negative_prompt": "text"}, 4.0) == "text"
        """);

    /// <summary>
    /// The retry for a pipeline that rejects an argument: the progress callback may go, the
    /// negative prompt may not — for a recipe that never declared <c>negativePrompt: false</c>.
    /// </summary>
    [PythonNumpyFact]
    public void TheRetryDropsTheProgressCallbackAndNeverTheNegativePrompt() => Run("""
        arguments = {"prompt": "p", "negative_prompt": "text", "callback_on_step_end": print}

        error = refused(dw.without_unsupported, TypeError("__call__() got an unexpected keyword argument 'negative_prompt'"), arguments)
        assert error.code == dw.ERROR_INVALID_REQUEST and "negative_prompt" in str(error), str(error)

        retry = dw.without_unsupported(TypeError("__call__() got an unexpected keyword argument 'callback_on_step_end'"), arguments)
        assert retry == {"prompt": "p", "negative_prompt": "text"}, retry
        assert "callback_on_step_end" in arguments, "the caller's dict must not be mutated"

        try:
            dw.without_unsupported(TypeError("something else entirely"), arguments)
            raise AssertionError("an unrelated TypeError was swallowed")
        except TypeError as error:
            assert "something else" in str(error)
        """);

    [PythonNumpyFact]
    public void TheSmallRecipeIsOfferedOnACpuOnlyBoxAndSd15IsNoLongerTheOnlyOne() => Run("""
        assert lcm["pipeline"] == "LatentConsistencyModelPipeline"
        assert lcm["defaults"]["steps"] == 4 and lcm["maxSteps"] == 8, lcm
        assert "variant" not in lcm, "the repo has no fp16 files; a variant would fall back loudly on every load"
        offered = dw.offered(recipes, "cpu")
        assert "lcm-dreamshaper" in offered and "sd15" in offered, offered
        assert [i for i in offered if not recipes[i].get("cpuViable")] == [], offered
        """);

    private static void Run(string body)
    {
        var python = PythonNumpyTestGate.Interpreter!;
        var root = RepositoryRoot();
        var script = Path.Combine(Path.GetTempPath(), $"inferhub-negative-{Guid.NewGuid():N}.py");

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
