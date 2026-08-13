# InferHub v3.22.0 — the plans get the treatment the instructions got

**Nothing in this release changes what InferHub does.** No endpoint moved, no behaviour changed,
`git diff src/` is empty, and no container image was rebuilt or pulled. It is the second half of
v3.20: that release split the 2 984-line `CLAUDE.md` an agent loads before it does anything, and left
the *build briefs* — the documents that produce the work in the first place — exactly as they were.

## The problem, in three numbers

| | Lines |
|---|---:|
| `plan/roadmap-v3.14-to-v3.19-image-generation.md` | 162 KB of one file |
| `plan/phase-53-chunked-upload.md` (a single phase) | **519** |
| of which §6, *written after the release had already shipped* | ~120 |

Three things were wrong, and only the first is about size.

**§6 was in the wrong document.** A phase brief ended with its own verification results — the numbers
observed on the box, what did not get run — appended after the release. That is what release notes
are, and they were being written anyway.

**The brief re-argued the rules.** Every phase restated the constraints it brushed, so a rule amended
in a later phase left stale copies in two roadmaps. Phase 52 spent a whole release forbidding exactly
that between context files (*one home per decision, pointers everywhere else*) and the plan folder
was outside its reach.

**And the format itself was in the root file** — 52 lines of "how to write a plan", 13% of the root's
400-line budget, loaded into every session including the overwhelming majority that never write one.

## What changed

The format moved to **`plan/CLAUDE.md`**, next to the thing it constrains, where it is loaded by
somebody already writing a brief and by nobody else. The root file went **395 → 339 lines**.

A brief is now: header, goal, decisions as claims, tasks, done-when, release. **There is no §6.** The
argument behind a decision goes into the area's `CLAUDE.md` when the phase lands — one home, as
before — and the brief keeps the claim and the rejected alternative, which is what an implementer
needs *before* the code exists.

The first two written this way: **171 lines** (phase 54) and **185** (phase 55), against 519 for
phase 53 and 292 for phase 52.

**And a brief is written the day its phase starts.** The three tracks before this one wrote every
phase up front; one of those files reached 162 KB, and phase 53 — written two days ahead of itself —
carries a §6 section listing *six things its brief got wrong or did not know*. A brief describing a
codebase two phases stale costs more to correct than to write. So a track file is now an **index**:
the order, the per-phase claim, the cut point, the invariants — and not the phases.

## Tested, because prose has no compiler

Three checks joined `ContextContractTests`:

- **`EveryLeanPlanFitsItsBudget`** — 250 lines for any brief declaring `Format: lean`.
- **`EveryPhaseBriefDeclaresItsFormat`** — every `plan/phase-NN` above 53 carries the marker.
- **`EveryPhaseStatusMatchesTheOverview`** — a brief's `Status:` and its row in the index agree.
  This is the one that drifts in practice: the brief gets flipped at the end of a release and the
  table is the step somebody skips.

**The marker lives in the brief rather than in an array in the test.** A list is a second place to
update on the day a plan is written, and forgetting it fails *silently* — the file is simply never
checked. Forgetting the marker fails on a screen.

`EveryContextFileIsWithinItsBudget` now covers `plan/CLAUDE.md` at the scoped budget.

## The gitignore exception, and what it costs

`/plan/` is gitignored — the briefs are internal and stay internal. But the root file **names** every
scoped context file and `EveryCrossAreaPointerResolves` follows those pointers, so a format file that
vanished in a fresh clone would fail CI, and fail it *correctly*: a named file that is not there
teaches a reader the context does not exist rather than that the index is wrong.

So exactly one path is excepted — `/plan/*` with `!/plan/CLAUDE.md`, per-entry because git does not
descend into an excluded directory. Committing all 0.8 MB of briefs was considered and is a
publishing decision, not a side effect of a docs phase.

**The cost, stated rather than buried:** the three checks above run only where the briefs exist — a
maintainer's clone — behind `PlanFolderFactAttribute`. In CI they are **skipped, not passed**. A
green tick from a job that has never seen the directory would mean nothing, and this project counts
its skips in the release notes for exactly that reason.

## Verification

- `dotnet test tests/InferHub.Tests.Shared` — **77 passed, 0 failed, 0 skipped** (277 ms). The phase's
  own test slice, which is also the first application of the new track's rule that a phase re-runs
  the slice it touched and the fleet-wide check is a release of its own.
- **The guard was checked against a real difference**: the overview row for phase 55 was flipped to
  `DONE` by hand, and `EveryPhaseStatusMatchesTheOverview` failed naming both files and both values.
  A check that has drifted from its subject is worse than none, so it was made to fail on purpose
  before being trusted.
- `git status` under `plan/` shows `plan/CLAUDE.md` untracked and every brief still ignored —
  `git check-ignore -v` confirms which rule claims each.

**Not established, said out loud:**

- **No image was pulled and none was rebuilt**, because nothing in any image changed. This is the
  narrowed artifact check narrowing to nothing, and it is written here rather than passed over in
  silence.
- **The three plan checks have never run in CI** and by construction never will.
- **`README.md` was not changed**, because nothing in it describes the plan format — it has no
  contributor section and never mentions `CLAUDE.md`. Checked rather than assumed; v3.20, the same
  kind of release, changed it for the same reason: not at all.
- The 400-line root budget is unchanged. It was **not** tightened to the new 339, because a budget
  fitted to the present is a budget that never binds.
