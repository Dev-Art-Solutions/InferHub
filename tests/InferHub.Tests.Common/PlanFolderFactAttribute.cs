namespace InferHub.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly) when the repository has no <c>plan/</c>
/// folder. Same shape as <c>PythonWorkerFactAttribute</c> and for the same reason: no new
/// dependency (design rule 5).
/// </summary>
/// <remarks>
/// <para>
/// Phase 54. The build briefs are gitignored — only <c>plan/CLAUDE.md</c> is committed, because the
/// root file names it and <c>EveryCrossAreaPointerResolves</c> has to be able to follow that pointer
/// in a fresh clone (54 D3). So the checks over the briefs themselves can only run on a maintainer's
/// machine, and in CI they are <b>skipped, not passed</b> — the distinction tests/CLAUDE.md insists
/// on, and the number belongs in the release notes like every other gate's.
/// </para>
/// <para>
/// <b>The weaker guarantee is the point of writing it down here rather than quietly.</b> A brief
/// that breaks its budget is caught by whoever is actually writing briefs, which is the only person
/// who can fix it; asking CI to police a directory it has never seen would be asking for a green
/// tick that means nothing.
/// </para>
/// </remarks>
public sealed class PlanFolderFactAttribute : FactAttribute
{
    public PlanFolderFactAttribute()
    {
        if (!PlanFolderTestGate.Exists)
        {
            Skip = "no plan/ briefs in this clone — they are gitignored, so this runs on a maintainer's machine only";
        }
    }
}

internal static class PlanFolderTestGate
{
    /// <summary>Whether this clone has the briefs. Probed once per test run.</summary>
    public static readonly bool Exists = Probe();

    private static bool Probe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        // A clone with only the committed plan/CLAUDE.md in it does not count: what these tests
        // check is the briefs, and there are none.
        return directory is not null
            && Directory.Exists(Path.Combine(directory.FullName, "plan"))
            && Directory.EnumerateFiles(Path.Combine(directory.FullName, "plan"), "phase-*.md").Any();
    }
}
