# plan/ — agent context

**Scope: `plan/`.** The shape every build brief has, and where the parts of a phase end up once it
ships.

> **Read the root `CLAUDE.md` first.**

**The briefs themselves are not in the repository** — `/plan/*` is gitignored and this file is the
one exception, because it is the format rather than a brief (54 D3). So a fresh clone has the format
and no plans, and a `plan/phase-NN` cited by number from another context file is a pointer into the
maintainer's working copy. That is deliberate and it is the trade: the decisions those briefs argued
are all in the `CLAUDE.md` files, which is where a reader was going to look anyway.

## One phase → one file, written the day it starts

`plan/phase-NN-short-slug.md`, indexed in `plan/00-overview.md`. A multi-phase track gets a **thin
index** — `plan/roadmap-vX.Y-to-vX.Z-slug.md` — carrying the order, the per-phase claim, the cut
point and the invariants, and **not** the phases themselves.

**A brief is written on the day its phase starts, not up front with the track** (54 D1). The three
tracks before this one wrote every phase in advance; one of those files reached 162 KB, and phase 53
— written two days ahead of itself — has a §6 listing **six things its brief got wrong or did not
know**. A brief describing a codebase two phases stale costs more to correct than to write.

## The shape

**Header block** — `# Phase NN — <the claim, in a sentence> (vX.Y.Z)`, then a line carrying
`Status: TODO`, **`Format: lean`** (the marker the budget test keys on), the version, **Size** (S/M/L
+ days), what it depends on, and **`Test slice:`** — the test projects a reader must run, because
that is now the gate rather than the whole solution. Then repo link, the file's own path, its track,
and a `>` callout naming the decisions to read first **by number** ("49 D5", never "the panorama
phase").

**§1 Goal** — what is true today and why it is not enough, in the repo's own words and with the file
paths. Then the shape of the change, with a real command or payload. Then **Non-goals**, each written
as *a decision with its reason*, never a bare list.

**§2 Design decisions** — `### D1 — <a full sentence that states the claim>`, so a reader skimming
only headings gets the design. Each carries the reasoning, the **alternative that was considered and
rejected**, and which rule (1–7) it brushes. Mark the load-bearing one out loud. **Keep the body
short**: the durable argument goes to the area file when the phase lands (54 D2), and what belongs
here is what an implementer needs *before* the code exists.

**§3 Tasks** — `- [ ]` in dependency order, each naming a **real path**. Order them so a failure is
attributable: the thing that can break in isolation lands first. Always include the `CLAUDE.md`
amendment, the `appsettings.json` commented keys, README, and the `plan/00-overview.md` row.

**§4 Done when** — checkboxes, and they must include: *a deployment that changes no config behaves
identically*, **zero new `PackageReference` and `InferHub.Shared.csproj` still empty**, and **the
test slice green**. Anything that cannot be established from source says so out loud.

**§5 Release** — the checklist below, verbatim, every phase.

**There is no §6.** What a phase turned out to be goes in `.claude/release-notes-vX.Y.Z.md`, which is
where somebody looks for it and which is written anyway.

## §5 — the eight items, every phase, no exceptions

Fixed and ticked rather than prose to interpret, because this is the section a tired author skims.

- [ ] Bump `<Version>` in `Directory.Build.props`.
- [ ] `.claude/release-notes-vX.Y.Z.md` — including **what was not established, said out loud**.
- [ ] Tag `vX.Y.Z` → GitHub release.
- [ ] **Pull the published image and run the one thing this phase changed.** A phase that changed no
      image says so in the notes instead of going quiet.
- [ ] Flip `Status:` in the brief, the track index and `plan/00-overview.md`.
- [ ] `README.md`.
- [ ] `inferhub.devart.solutions` — changelog row, the `#idocs_*` section, "What's next".
- [ ] Blog post → FB → X.

README before the site because the site quotes it; the post last because it links the release.
**Batching the docs and the posts to the end of a track is how `.claude/social-v*.md` accumulated
unposted copy for a dozen releases** — copy written a week late describes what you remember rather
than what shipped, and the blog connector is **insert-only with a locking slug**, so it cannot be
corrected afterwards either. `list_posts` first, draft first, and **no shell commands in the post
HTML**: the Cloudflare WAF in front of the blog blocks the request, not the command.

## Budget

**A lean brief is 250 lines.** `ContextContractTests` enforces it on any file here declaring
`Format: lean`, checks every `plan/phase-*.md` above 53 declares it, and checks each brief's
`Status:` matches its row in `00-overview.md`. Those three run only where `plan/` exists — a
maintainer's clone — and are skipped, not passed, in CI.

The number is what a phase of this size actually needs once §6 and the re-argued rules are gone: 53's
brief was 570 lines and 55's is 185. **A budget fitted to the present never binds**, so this one is
deliberately below where a comfortable author would land.

## House voice

State the failure the decision prevents, concretely ("a client reads `error.message` and gets a wall
of backslashes"). Prefer a rejected alternative to an adjective. **Never write a caveat that a later
phase makes false without deleting it everywhere** — see the phase-35 note.

## Related context

- The rules a plan may not quietly amend: the root `CLAUDE.md`
- What a `Test slice:` line may name, and what each project holds: `tests/CLAUDE.md`

## Decisions recorded here

### Phase 54 (lean plans) — the format itself

**D1 — A brief is written the day its phase starts.** Above, with the 162 KB and the six wrong
guesses. **Considered and rejected: the previous three tracks' shape**, every phase written up front
in one file — it reads as thoroughness and it is mostly prediction.

**D2 — A decision is written into the scoped `CLAUDE.md` as the phase lands, and the brief keeps the
claim.** Before this, every decision was argued twice — once in the plan, once in `CLAUDE.md` — and
the two copies drifted the moment a later phase amended one. 52 D2 already forbids that *between*
context files; this is the same rule across the boundary. **Considered and rejected: dropping the
rejected alternatives from the brief too** — that is the one part that decays into "why on earth is
it like this" if it is not written while the alternative is still live.

**D3 — `plan/CLAUDE.md` is committed and the briefs are not.** The root file names every scoped
context file and `EveryCrossAreaPointerResolves` checks the pointer, so a format file that vanished
in a fresh clone would fail CI — and would fail it *correctly*, because a named file that is not
there teaches a reader that the context does not exist rather than that the index is wrong.
**Considered and rejected: committing all of `plan/`** — 0.8 MB of internal briefs, including the
candid post-mortems, is a publishing decision and not a side effect of a docs phase. The `/plan/*`
plus `!` form is required because git does not descend into an excluded *directory*.

**D4 — The twenty pre-54 briefs are left exactly as they are.** They are the record of what was
decided, several are cited by number from the context files, and a rewritten record says what we
would decide today — which is not what a record is for. **Considered and rejected: `plan/archive/`**,
which moves twenty cited paths to buy a tidier listing.
