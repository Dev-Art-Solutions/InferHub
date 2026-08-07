using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) when no Python interpreter is on PATH.
/// Same shape as <c>PostgresFactAttribute</c> / <c>OllamaSupervisorFactAttribute</c>, and for the
/// same reason: no Testcontainers, no <c>SkippableFact</c>, no new dependency (design rule 5).
/// </summary>
/// <remarks>
/// <b>Unlike the other gates, this one is not an opt-in — it is a capability probe</b>, because the
/// thing behind it is cheap and safe (a short-lived child process reading and writing its own
/// stdio) and because it needs to <em>run by default</em> in CI, where <c>python3</c> exists. A
/// gate that defaulted to off would have left the reference library exactly as untested as it was
/// when it shipped the v3.16.0 bug these tests exist to catch.
/// </remarks>
public sealed class PythonWorkerFactAttribute : FactAttribute
{
    public PythonWorkerFactAttribute()
    {
        if (PythonWorkerTestGate.Interpreter is null)
        {
            Skip = "no Python interpreter on PATH — the reference worker library cannot be driven here";
        }
    }
}

internal static class PythonWorkerTestGate
{
    /// <summary>The first interpreter that answers, or null. Probed once per test run.</summary>
    public static readonly string? Interpreter = Probe();

    private static string? Probe()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };

        foreach (var candidate in candidates)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, "-c \"import sys\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                });

                if (process is null)
                {
                    continue;
                }

                if (!process.WaitForExit(20_000))
                {
                    // A Windows Store alias stub that opens the app store and never exits is a real
                    // thing on developer machines; it must not hang the whole suite.
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
                // Not on PATH here. Try the next name, then skip.
            }
        }

        return null;
    }
}
