using System.Text.RegularExpressions;

namespace InferHub.Tests;

/// <summary>
/// Phase 52. The repository's <b>agent context</b> — the <c>CLAUDE.md</c> files — held to the same
/// standard as its code.
/// </summary>
/// <remarks>
/// <para>
/// The problem this exists for is the one phase-45 D1 described about the console, one level up:
/// <b>a decision nobody can find is a decision nobody applies</b>. Before this phase the root file
/// was 2 984 lines and every session loaded all of it; splitting it by directory is what makes it
/// affordable, and splitting it is also the one operation that can <em>silently lose</em> a
/// decision. Text has no compiler.
/// </para>
/// <para>
/// So the inventory in <c>tests/phase-inventory.txt</c> is generated once from the pre-split file
/// and checked in, and the assertions below are the net: every block that existed still exists,
/// <b>exactly once</b>, and every cross-area pointer resolves. This is
/// <c>ConsoleContractTests</c>'s guard-on-the-guard discipline (phase 51) applied to prose — a
/// table of contents that has drifted from its subject is worse than none, because it reads as
/// coverage.
/// </para>
/// </remarks>
public class ContextContractTests
{
    /// <summary>
    /// How big each file may get. <b>A budget is the only thing that stops this phase being undone
    /// one paragraph at a time</b>, which is precisely how the original reached 2 984 lines: nobody
    /// ever added more than a section.
    /// </summary>
    /// <remarks>
    /// The root's is the tightest because it is the one loaded <em>every</em> session regardless of
    /// what anybody is working on. The scoped ones are loaded only by somebody already in that
    /// subtree, so they can afford the detail — that asymmetry is the whole design.
    /// </remarks>
    /// <remarks>
    /// <b>1100 is chosen so that today's two biggest areas fit with headroom and a third of their
    /// size again forces a split.</b> It is deliberately not the size they happen to be — a budget
    /// fitted to the present is a budget that never binds — and it is deliberately not tighter,
    /// because a limit that fails on the next honest paragraph gets raised rather than obeyed.
    /// </remarks>
    private const int ScopedBudget = 1100;

    /// <summary>
    /// Phase 54. What a build brief may grow to, once its verification results live in the release
    /// notes and the rules it brushes are pointed at rather than re-argued.
    /// </summary>
    /// <remarks>
    /// Deliberately below where a comfortable author would land: phase 53's brief was 570 lines,
    /// phase 55's is 185. <b>A budget fitted to the present is a budget that never binds</b> — the
    /// same reasoning as <see cref="ScopedBudget"/>, one document down.
    /// </remarks>
    private const int LeanPlanBudget = 250;

    private static readonly (string Path, int MaxLines)[] Budgets =
    [
        ("CLAUDE.md", 400),
        ("src/InferHub.Shared/CLAUDE.md", ScopedBudget),
        ("src/InferHub.Coordinator/CLAUDE.md", ScopedBudget),
        // Phase 62. The coordinator's file hit 1100 and the retrieval decisions were the largest
        // coherent subtree in it that the provider track had nothing to do with (62 D6).
        ("src/InferHub.Coordinator/Vector/CLAUDE.md", ScopedBudget),
        ("src/InferHub.Node/CLAUDE.md", ScopedBudget),
        // Phase 67. The node's file hit 1099 and the tool runtime with its media phases was the
        // largest coherent subtree the provider track had nothing to do with (67 D6).
        ("src/InferHub.Node/Tools/CLAUDE.md", ScopedBudget),
        ("python/CLAUDE.md", ScopedBudget),
        ("tests/CLAUDE.md", ScopedBudget),
        ("deploy/CLAUDE.md", ScopedBudget),
        ("plan/CLAUDE.md", ScopedBudget)
    ];

    /// <summary>
    /// The first phase written in the lean format. Everything numbered above it is held to it;
    /// everything below is the record of what was decided and is left alone (54 D4).
    /// </summary>
    private const int FirstLeanPhase = 54;

    /// <summary>
    /// <b>The irreversible failure, made impossible to do quietly.</b> Every decision block that
    /// existed before the split still exists, in exactly one file.
    /// </summary>
    /// <remarks>
    /// "Exactly one" is not tidiness — it is D2. A decision that appears in two files is two copies
    /// that drift, and the day they disagree the reader believes whichever one their working
    /// directory happened to load. Cross-area decisions get a <em>pointer</em>, which cannot rot
    /// into a contradiction; it can only rot into a broken link, which the test below catches.
    /// </remarks>
    [Fact]
    public void EveryPhaseDecisionBlockSurvivesTheSplitExactlyOnce()
    {
        var expected = Inventory();
        var files = ContextFiles();

        Assert.NotEmpty(expected);

        foreach (var (phase, heading) in expected)
        {
            var holders = files
                .Where(file => Regex.IsMatch(
                    File.ReadAllText(file.Path),
                    // The boundary goes immediately after the digits, not after the space that
                    // follows them: a heading reads "### Phase 21 (OpenAI…", and between a space
                    // and an opening bracket there is no word boundary at all. Anchored this way
                    // "Phase 2" also cannot match "Phase 21", which is the case that matters.
                    $@"^### Phase {phase}\b",
                    RegexOptions.Multiline))
                .Select(file => file.Relative)
                .ToArray();

            Assert.True(
                holders.Length == 1,
                holders.Length == 0
                    ? $"Phase {phase} {heading} — its decisions are in NO CLAUDE.md. They were lost in a move."
                    : $"Phase {phase} is in {holders.Length} files ({string.Join(", ", holders)}). "
                      + "A decision lives in one place and is pointed at from the others (52 D2).");
        }
    }

    /// <summary>
    /// Every <c>see …/CLAUDE.md</c> pointer resolves to a file that exists.
    /// </summary>
    /// <remarks>
    /// The failure mode a pointer trades for: it cannot contradict its target, but it can outlive
    /// it. This is the cheap half of that trade being paid.
    /// </remarks>
    [Fact]
    public void EveryCrossAreaPointerResolves()
    {
        var root = RepositoryRoot();
        var broken = new List<string>();

        foreach (var file in ContextFiles())
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file.Path), @"`([\w./-]+/CLAUDE\.md)`"))
            {
                var target = Path.Combine(root, match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(target))
                {
                    broken.Add($"{file.Relative} points at {match.Groups[1].Value}, which does not exist");
                }
            }
        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    /// <summary>
    /// Nobody has quietly grown a file back past its budget.
    /// </summary>
    [Fact]
    public void EveryContextFileIsWithinItsBudget()
    {
        var root = RepositoryRoot();
        var over = new List<string>();

        foreach (var (relative, max) in Budgets)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                continue;
            }

            var lines = File.ReadAllLines(path).Length;

            if (lines > max)
            {
                over.Add($"{relative} is {lines} lines, over its {max}-line budget. "
                         + "Move something to the area it constrains, or argue the budget up in 52 D5.");
            }
        }

        Assert.True(over.Count == 0, string.Join("\n", over));
    }

    /// <summary>
    /// The root's index names every scoped file, and every file it names exists — in both
    /// directions, because each failure is invisible from the other side.
    /// </summary>
    /// <remarks>
    /// An index missing a file means an agent never learns that context exists. An index naming a
    /// file that does not means an agent goes looking for it and finds nothing, which is worse:
    /// they conclude the context does not exist rather than that the index is wrong.
    /// </remarks>
    [Fact]
    public void TheRootIndexAndTheScopedFilesAgree()
    {
        var root = RepositoryRoot();
        var rootFile = Path.Combine(root, "CLAUDE.md");
        var text = File.ReadAllText(rootFile);

        var onDisk = ContextFiles()
            .Where(file => !string.Equals(file.Relative, "CLAUDE.md", StringComparison.Ordinal))
            .Select(file => file.Relative)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // Before the split there is nothing to index, and that is a legitimate state rather than a
        // failure — this test starts meaning something the moment the first scoped file lands.
        if (onDisk.Length == 0)
        {
            return;
        }

        var missing = onDisk.Where(name => !text.Contains(name, StringComparison.Ordinal)).ToArray();

        Assert.True(
            missing.Length == 0,
            $"the root CLAUDE.md does not name: {string.Join(", ", missing)} — "
            + "an agent never learns that context exists.");
    }

    /// <summary>
    /// Phase 54. No lean brief has grown back past its budget.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is the one that produced a 162 KB roadmap: nobody ever added more
    /// than a section. It is the same sentence as <see cref="EveryContextFileIsWithinItsBudget"/>
    /// because it is the same failure, and the fix is the same too — the verification results belong
    /// in the release notes and the rules belong in the file that owns them.
    /// </remarks>
    [PlanFolderFact]
    public void EveryLeanPlanFitsItsBudget()
    {
        var over = LeanPlans()
            .Where(plan => File.ReadAllLines(plan.Path).Length > LeanPlanBudget)
            .Select(plan => $"{plan.Relative} is {File.ReadAllLines(plan.Path).Length} lines, over "
                            + $"the {LeanPlanBudget}-line budget. Move the verification results to the "
                            + "release notes and the argument to the area's CLAUDE.md (54 D2).")
            .ToArray();

        Assert.True(over.Length == 0, string.Join("\n", over));
    }

    /// <summary>
    /// Phase 54. Every brief from 54 onward declares <c>Format: lean</c>.
    /// </summary>
    /// <remarks>
    /// <b>The marker travels with the document rather than living in a list here</b> (54 D3's
    /// mechanism, applied to the budget): an array in this file is a second place to update on the
    /// day a brief is written, and forgetting it fails <em>silently</em> — the brief is simply never
    /// checked. Forgetting the marker fails here instead, which is a sentence on a screen.
    /// </remarks>
    [PlanFolderFact]
    public void EveryPhaseBriefDeclaresItsFormat()
    {
        var undeclared = PhaseBriefs()
            .Where(brief => brief.Phase >= FirstLeanPhase && !IsLean(brief.Path))
            .Select(brief => $"{brief.Relative} is phase {brief.Phase} and does not declare "
                             + "`Format: lean` in its header block, so nothing holds it to a budget.")
            .ToArray();

        Assert.True(undeclared.Length == 0, string.Join("\n", undeclared));
    }

    /// <summary>
    /// Phase 54. A brief's <c>Status:</c> and its row in <c>plan/00-overview.md</c> say the same
    /// thing.
    /// </summary>
    /// <remarks>
    /// Two places recording one fact, which is the shape 52 D2 spent a phase removing from the
    /// context files — but here the duplication is load-bearing (the index has to be readable on its
    /// own), so it is tested instead. It drifts in exactly one direction: the brief is flipped at the
    /// end of a release and the table is the step somebody skips.
    /// </remarks>
    [PlanFolderFact]
    public void EveryPhaseStatusMatchesTheOverview()
    {
        var overview = File.ReadAllLines(Path.Combine(RepositoryRoot(), "plan", "00-overview.md"));
        var drifted = new List<string>();

        foreach (var brief in PhaseBriefs().Where(brief => brief.Phase >= FirstLeanPhase))
        {
            if (Status(File.ReadAllText(brief.Path)) is not { } declared)
            {
                drifted.Add($"{brief.Relative} has no `Status:` line in its header block.");
                continue;
            }

            var row = overview.FirstOrDefault(line => Regex.IsMatch(line, $@"^\|\s*{brief.Phase}\s*\|"));

            if (row is null)
            {
                drifted.Add($"{brief.Relative} is not indexed in plan/00-overview.md — "
                            + "an agent never learns the phase exists.");
                continue;
            }

            var cells = row.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var indexed = cells[^1];

            // Compared on the leading token and, for a shipped phase, the version — not character
            // for character. What is being caught is a brief that says DONE beside a table that
            // still says TODO, and holding the two to an identical string would fail on a date
            // written two ways instead.
            if (!indexed.StartsWith(declared.Token, StringComparison.Ordinal)
                || (declared.Version is { } version && !indexed.Contains(version, StringComparison.Ordinal)))
            {
                drifted.Add($"{brief.Relative} says `Status: {declared.Token}"
                            + (declared.Version is null ? "" : $" ({declared.Version})")
                            + $"` and plan/00-overview.md says `{indexed}`.");
            }
        }

        Assert.True(drifted.Count == 0, string.Join("\n", drifted));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Every markdown file under <c>plan/</c> declaring the lean marker.</summary>
    private static IReadOnlyList<(string Path, string Relative)> LeanPlans() =>
        PlanFiles().Where(plan => IsLean(plan.Path)).ToArray();

    /// <summary>Every <c>plan/phase-NN-*.md</c>, with the number parsed from the file name.</summary>
    private static IReadOnlyList<(string Path, string Relative, int Phase)> PhaseBriefs() =>
        PlanFiles()
            .Select(plan => (plan.Path, plan.Relative, Match: Regex.Match(plan.Relative, @"/phase-(\d+)-")))
            .Where(plan => plan.Match.Success)
            .Select(plan => (plan.Path, plan.Relative, int.Parse(plan.Match.Groups[1].Value)))
            .ToArray();

    private static IReadOnlyList<(string Path, string Relative)> PlanFiles()
    {
        var directory = Path.Combine(RepositoryRoot(), "plan");

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md")
                .Select(path => (path, Path.GetRelativePath(RepositoryRoot(), path).Replace('\\', '/')))
                .OrderBy(plan => plan.Item2, StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    /// <summary>
    /// The marker is looked for in the header block only — the first dozen lines — so that a brief
    /// merely <em>discussing</em> the format (this phase's own does) is not mistaken for one written
    /// in it.
    /// </summary>
    private static bool IsLean(string path) =>
        File.ReadLines(path).Take(12).Any(line => line.Contains("`Format: lean`", StringComparison.Ordinal));

    private static (string Token, string? Version)? Status(string brief)
    {
        var match = Regex.Match(brief, @"Status:\s*(TODO|DONE)\b[^`\n]*?(v\d+\.\d+\.\d+)?");

        return match.Success
            ? (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null)
            : null;
    }

    private static IReadOnlyList<(int Phase, string Heading)> Inventory()
    {
        var path = Path.Combine(RepositoryRoot(), "tests", "phase-inventory.txt");

        return File.ReadAllLines(path)
            .Where(line => line.Length > 0 && char.IsDigit(line[0]))
            .Select(line => line.Split('\t', 2))
            .Select(parts => (int.Parse(parts[0]), parts.Length > 1 ? parts[1] : string.Empty))
            .ToArray();
    }

    /// <summary>
    /// Every <c>CLAUDE.md</c> in the repository, excluding build output. Discovered rather than
    /// listed, so a file added without being indexed is caught by the test above rather than being
    /// invisible to it.
    /// </summary>
    private static IReadOnlyList<(string Path, string Relative)> ContextFiles()
    {
        var root = RepositoryRoot();

        return Directory.EnumerateFiles(root, "CLAUDE.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (path, Path.GetRelativePath(root, path).Replace('\\', '/')))
            .OrderBy(file => file.Item2, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test assembly.");
    }
}
