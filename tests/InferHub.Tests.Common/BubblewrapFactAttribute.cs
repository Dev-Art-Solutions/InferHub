using System.Diagnostics;

namespace InferHub.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) when <c>bwrap</c> is not on <c>PATH</c> or this
/// is not Linux. Same shape as <c>PythonWorkerFactAttribute</c> / <c>PostgresFactAttribute</c>: no
/// Testcontainers, no <c>SkippableFact</c>, no new dependency (design rule 5) — a probe, not an
/// opt-in, because the CI Linux runner has <c>bubblewrap</c> installed and the real assertion this
/// phase exists to make (a sandboxed worker cannot read a file outside its declared binds) must run
/// there by default rather than needing somebody to flip a switch.
/// </summary>
public sealed class BubblewrapFactAttribute : FactAttribute
{
    public BubblewrapFactAttribute()
    {
        if (BubblewrapTestGate.Path is null)
        {
            Skip = BubblewrapTestGate.SkipReason;
        }
    }
}

internal static class BubblewrapTestGate
{
    public const string SkipReason =
        "bwrap is not on PATH here (Linux-only, phase-83 D1) — the sandbox cannot be exercised for real on this machine";

    public static readonly string? Path = Probe();

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("bwrap", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(10_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                return null;
            }

            return process.ExitCode == 0 ? "bwrap" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
