using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) unless a Python interpreter with <c>numpy</c>
/// and <c>Pillow</c> can be found — the two libraries the diffusion worker's pixel arithmetic runs on
/// (phase 88).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PythonWorkerFactAttribute"/> needs only an interpreter, because the reference library
/// is stdlib-only. The worker's <em>pure</em> functions — the seam metric, the blend, the cubemap —
/// need numpy, and until this gate they were checked by hand on the day and never again (the v3.23
/// release notes are the record of that). Same shape as the other gates: no new dependency, a
/// capability probe rather than an opt-in, and CI installs the two packages so it <b>runs</b> there.
/// </para>
/// <para>
/// <c>INFERHUB_TEST_PYTHON</c> names the interpreter outright. On a developer box the first
/// <c>python</c> on PATH is often somebody's project venv without numpy in it, and the right answer
/// is to point at a real one, not to make the probe guess.
/// </para>
/// </remarks>
public sealed class PythonNumpyFactAttribute : FactAttribute
{
    public PythonNumpyFactAttribute()
    {
        if (PythonNumpyTestGate.Interpreter is null)
        {
            Skip = "no Python interpreter with numpy and Pillow (set INFERHUB_TEST_PYTHON to one) — " +
                   "the diffusion worker's pixel arithmetic cannot be run here";
        }
    }
}

internal static class PythonNumpyTestGate
{
    /// <summary>The first interpreter that can import both, or null. Probed once per test run.</summary>
    public static readonly string? Interpreter = Probe();

    private static string? Probe()
    {
        var configured = Environment.GetEnvironmentVariable("INFERHUB_TEST_PYTHON");

        var candidates = !string.IsNullOrWhiteSpace(configured)
            ? new[] { configured }
            : OperatingSystem.IsWindows()
                ? new[] { "python.exe", "python3.exe" }
                : new[] { "python3", "python" };

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, "-c \"import numpy, PIL\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });

                if (process is null)
                {
                    continue;
                }

                if (!process.WaitForExit(30_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                    }

                    continue;
                }

                if (process.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Not there. Try the next name, then skip.
            }
        }

        return null;
    }
}
