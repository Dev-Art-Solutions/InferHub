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

    private static readonly (string Path, int MaxLines)[] Budgets =
    [
        ("CLAUDE.md", 400),
        ("src/InferHub.Shared/CLAUDE.md", ScopedBudget),
        ("src/InferHub.Coordinator/CLAUDE.md", ScopedBudget),
        ("src/InferHub.Node/CLAUDE.md", ScopedBudget),
        ("python/CLAUDE.md", ScopedBudget),
        ("tests/CLAUDE.md", ScopedBudget),
        ("deploy/CLAUDE.md", ScopedBudget)
    ];

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

    // ---- helpers ---------------------------------------------------------------------------------

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
