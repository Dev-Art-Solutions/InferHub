# InferHub v3.20.0 — 64k tokens of instructions, and why we split them up

**Nothing in this release changes what InferHub does.** No endpoint moved, no behaviour changed, and
`git diff src/` is four lines of `InternalsVisibleTo`. It is a release about the repository itself,
and it is here because the cost it removes had grown into the largest recurring tax on the project.

## The problem, in one number

`CLAUDE.md` — the file an AI agent reads before it does anything in this repo — had reached
**2 984 lines, 230 KB, roughly 64 000 tokens**. It was loaded **in full, into every session, before
a single question was asked**. 88% of it was decision history: thirty `### Phase NN` blocks
accumulated over sixty releases, each of them worth keeping and almost none of them relevant to the
task at hand.

An agent asked to fix a typo in a Python worker was reading the Qdrant connector's UUID mapping, the
cluster lease's split-brain fence and the mask convention on the way in.

## The fix is not "write less"

The obvious response — trim it — is wrong. Every one of those blocks records *why* something is the
way it is, and this project has been burned repeatedly by decisions whose reasoning was lost. The
cost is not **having** the context. It is **loading** it.

So the file split seven ways, along the directory tree:

| Working in | Also loads | Tokens | Saved |
|---|---|---:|---:|
| repo root | — | 7 740 | **87%** |
| `python/` | `python/CLAUDE.md` | 12 820 | **79%** |
| `src/InferHub.Shared/` | its own | 19 420 | **69%** |
| `src/InferHub.Coordinator/` | its own | 27 470 | **57%** |
| `src/InferHub.Node/` | its own | 30 020 | **53%** |
| `tests/` | `tests/CLAUDE.md` | 9 040 | **85%** |
| `deploy/` | `deploy/CLAUDE.md` | 8 870 | **86%** |

**The split axis has to be the directory tree**, because that is what the loader keys on. Splitting
by phase — `docs/decisions/phase-46.md` plus an index — is the tidier archive and saves nothing: a
reader working on the node does not know which phase numbers touched the node, so they load the
index, guess, and open three files. Automatic pickup is the entire mechanism; a split the loader
cannot see is a filing system, not a context strategy.

**The seven design rules stay in the root, whole.** They bind every area, and they are what
amendments attach to.

## A decision has one home

Most blocks belong to exactly one area. The ones that do not are pointed at, **never copied** — two
copies of a decision are two copies that drift, and the day they disagree the reader believes
whichever their working directory happened to load.

That rule needs enforcing, because prose has no compiler. So `ContextContractTests` holds a
checked-in inventory of every block that existed before the split and asserts each still exists
**exactly once**, that every pointer resolves, that the index and the files agree in both
directions, and that nothing has grown back past its budget — **400 lines for the root, 1100 for a
scoped file**.

A budget is the only thing that stops this being undone one paragraph at a time, which is exactly
how the original reached 2 984 lines: nobody ever added more than a section.

## Tests you can run one of

The other half. One project of 124 files and 1243 tests became four, plus a fixture library:

```
dotnet test tests/InferHub.Tests.Shared        # 2.0s
dotnet test tests/InferHub.Tests.Coordinator   # 4.5s
dotnet test tests/InferHub.Tests.Node          # 34s
dotnet test tests/InferHub.Tests.Mesh          # 43s — real Kestrel, real SignalR, real processes
dotnet test InferHub.sln                       # everything, in parallel: 44s
```

Before, every edit cost 41 seconds regardless of what it touched. **`Mesh` is what justifies the
other three**: it holds everything that opens a socket or spawns a process, and separating it means
a mesh failure is *visibly* a mesh failure rather than one red line in a run of twelve hundred.

**Considered and rejected: xUnit traits and `--filter`.** Smaller change, less than half the
benefit — `--filter` still builds the whole 23 000-line assembly, still recompiles on any edit, and
still puts two agents in one `.csproj`. The trait is a label on a monolith.

**What it does not buy is build isolation**, and saying so matters: the fixture library references
both hosts, so editing the coordinator still rebuilds every suite. What you get is a run you can
scope and files two agents do not share.

## Two things this found

**The file count was wrong, and the compiler said so.** The plan counted 93 test files; there were
124. `ls *.cs` sees the top level, and two subdirectories held 33 more. Nothing was lost — the build
stopped resolving a namespace within a minute of the move.

**CI would have gone green testing nothing.** The workflow said
`dotnet test tests/InferHub.Tests` — a path that, after the split, names nothing at all. That does
not fail; it passes, having run zero tests. It now names the solution, so adding a project is enough
to get it run and there is no list to forget.

## Upgrading

Nothing to do, and nothing to check. The images are byte-identical apart from the version stamp;
`git diff src/` is four `InternalsVisibleTo` lines.

---

Test count: **1247**, up from 1243 — the four new context tests are the entire difference.
1199 passed, 48 skipped, 0 failed. Zero new dependencies.
