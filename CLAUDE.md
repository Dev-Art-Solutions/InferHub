# CLAUDE.md

Guidance for Claude Code when working in this repository. Keep it focused on what is
non-obvious — README.md has the user-facing pitch and config reference.

## What this is

InferHub is a self-hosted, Ollama-compatible inference mesh. A **coordinator** runs on an
always-on, GPU-less host and exposes Ollama-shaped HTTP endpoints; **nodes** run on GPU
machines, reach out to the coordinator over SignalR, and execute prompts against a local
inference backend (Ollama today, pluggable). No port forwarding on the node side.

## Solution layout

```
src/
  InferHub.Shared/        Contracts + Ollama DTOs + OpenAI DTOs/translators (both ends speak it),
                          and since phase 38 the whole *pure* retrieval core: Vector/ (IVectorStore,
                          LocalVectorStore, InvertedIndex, HybridSearch, RetrievalPipeline) and
                          Ingestion/ (TextExtractor, Chunker, DocumentIndex, IngestionPipeline).
                          Phase 44 added Vector/Qdrant/ (QdrantVectorStore, QdrantClient, QdrantIdMap,
                          SparseVector) — free only because phase-33 D2 hand-rolled the connector
                          instead of taking the gRPC client.
                          Still a plain class library with ZERO package references — see rule 2.
  InferHub.Coordinator/   ASP.NET Core web app (Sdk.Web). HTTP + SignalR hub + routing. Keeps the
                          external vector providers, replication/healing, endpoints, Metrics and
                          PdfTextExtractor.
  InferHub.Node/          Worker service (Sdk.Worker). SignalR client + backend driver. LocalApi/
                          is solo mode (phase 37); Retrieval/ is solo RAG (phase 38). Two
                          Dockerfiles: the plain one (multi-arch, ~340 MB) and Dockerfile.ollama
                          (phase 39 — amd64, ~4 GB, Ollama inside the container).
  InferHub.Node.WindowsService/  Windows-service host. References InferHub.Node, adds AddWindowsService + install scripts.
tests/
  InferHub.Tests.Common/       fixtures and hosts. A library, not a test project.
  InferHub.Tests.Shared/       pure: contracts, renderers, stores, the context contract
  InferHub.Tests.Coordinator/  endpoints, routing, vector, cluster, metrics, console
  InferHub.Tests.Node/         backends, supervisor, tool runtime, solo, profiles
  InferHub.Tests.Mesh/         real Kestrel + real SignalR + real child processes
                               See tests/CLAUDE.md. Split in phase 52 (D3).
deploy/
  docker/                 Compose stack (coordinator + node), Postgres overlay, runbook.
  postgres/               Postgres+pgvector for the gated integration tests.
  windows/                Node-as-a-Windows-service install scripts.
.github/workflows/        CI: build+test and docker image build on PRs; GHCR publish on v* tags.
plan/                     Phase build-briefs. Not shipped; lives in repo for context.
tools/
  InferHub.Eval/          Retrieval eval harness (phase 24). Standalone console, no project refs, NOT in the images.
  InferHub.Migrate/       Cross-provider vector migration (phase 35). Standalone console; references the
                          Coordinator to compose real stores through the one composition root. NOT in the images.
```

- TFM: `net10.0`, `Nullable enable`, `LangVersion latest` — set in [Directory.Build.props](Directory.Build.props).
- Solution version is also in `Directory.Build.props` (`<Version>`); bump it when tagging a release.
- `InferHub.Coordinator`, `InferHub.Node` and `InferHub.Migrate` grant `InternalsVisibleTo` to all
  five test assemblies — tests can reach internals. **Add a test project and you must add it there
  too**, or it fails at compile time, which is the right failure.

## Build / test / run

```powershell
dotnet build InferHub.sln
dotnet test  InferHub.sln                      # everything, in parallel, as CI does

# or just the slice you changed — the point of phase 52
dotnet test  tests/InferHub.Tests.Shared       # ~2s
dotnet test  tests/InferHub.Tests.Coordinator  # ~5s
dotnet test  tests/InferHub.Tests.Node
dotnet test  tests/InferHub.Tests.Mesh         # the slow, honest one

# two terminals for a local end-to-end:
dotnet run --project src/InferHub.Coordinator    # http://localhost:5080
dotnet run --project src/InferHub.Node           # talks to Ollama on :11434
```

Loopback skips auth by default (`Auth:RequireAuthForLoopback=false`) — local curl just
works. Set keys via env vars or user-secrets (`dotnet user-secrets`); never commit secrets
into `appsettings.json`.

## Where the rest of the context is

**This file holds what is true everywhere. Everything else lives next to the code it constrains,**
and your editor loads it when you work in that subtree — so an agent fixing a Python worker no
longer pays for the Qdrant connector's UUID mapping and the cluster lease's split-brain fence.

| Working in | Also read | Holds |
|---|---|---|
| `src/InferHub.Shared/` | `src/InferHub.Shared/CLAUDE.md` | contracts, the OpenAI/Ollama dialects, the retrieval core, the vector stores, the image envelope · phases 24, 29, 33, 34, 40, 46, 47 |
| `src/InferHub.Coordinator/` | `src/InferHub.Coordinator/CLAUDE.md` | endpoints, routing, admission, cluster, `/metrics`, the console · phases 21–23, 25, 26, 28, 30–32, 35, 44, 45, 51 |
| `src/InferHub.Node/` | `src/InferHub.Node/CLAUDE.md` | backends, the Ollama supervisor, solo mode, the tool runtime, profiles · phases 36–39, 41–43, 48 |
| `python/` | `python/CLAUDE.md` | the worker protocol, recipes, the diffusion worker · phases 49, 50 |
| `tests/` | `tests/CLAUDE.md` | the four test projects and the testing discipline |
| `deploy/`, any Dockerfile | `deploy/CLAUDE.md` | the five images and the permissions trap |

**A decision lives in exactly one of those files** and is pointed at from the others (52 D2).
`ContextContractTests` fails if one is lost, duplicated, or pointed at and missing.

## Design rules to preserve

These come from `plan/00-overview.md` and the way the code is shaped today. Treat them
as load-bearing:

1. **Pluggable backends stay backend-agnostic.** Anything Ollama-specific belongs in
   `Backends/OllamaBackend.cs` or behind `IInferenceBackend`. Do not let
   `OllamaClient` types leak into `Worker`, `CoordinatorConnection`, or the coordinator.
2. **Core stays host-agnostic.** `InferHub.Shared` is a plain class library and no ASP.NET types
   go in it, or in node *configuration*. **Amended in phase 37:** ASP.NET now appears in
   `InferHub.Node`, confined to `LocalApi/` and only when solo mode is on. What the rule was
   protecting survives — the shared contracts and the options classes are still plain — and rule 5
   survives with it, because `Microsoft.AspNetCore.App` is a `FrameworkReference`, not a package.
   If you find yourself putting an `IResult` or an `HttpContext` anywhere else on the node, stop.

   **Phase 38 tested the rule properly and it held.** The retrieval core moved into
   `InferHub.Shared` so a solo node runs the same code rather than a second copy — and a plain
   class library cannot see `ILogger` or `IOptions<T>` without taking two packages. It did not take
   them: [IVectorLog](src/InferHub.Shared/Vector/IVectorLog.cs) is the two-method logging seam each
   host adapts in three lines, and the pipelines take **plain options objects** while the
   `IValidateOptions<T>` validators stay per-host. `InferHub.Shared.csproj` is still an empty
   `<Project Sdk="Microsoft.NET.Sdk">`, and it must stay that way: the day it grows a
   `PackageReference` is the day "the shared library is free" stops being true.
3. **Build-free UI.** Static assets only. Reusing CSS variables across `status.html` and
   `console.html` is intentional.
4. **No persisted state, *except* the vector store.** Registry, affinity, audit log, and
   metrics are all in-memory; a coordinator restart still resets the fleet view. The
   one exception is the vector store, which now has **two providers** selected by
   `VectorStore:Provider` (`local` default, `postgres` since phase 20):
   - **`local`** — the phase-13 shape. Vector records persist to `VectorStore:DataDirectory`
     as a plain raw store (append-only ops log + periodic compacted snapshots), the in-memory
     index is rebuilt from it on startup, and (phase 15) assigned node replicas persist under
     `Vector:ReplicaDirectory`. The hub's raw store is authoritative; node replicas are derived.
   - **`postgres`** — an **external** durable store (PostgreSQL + pgvector). The coordinator
     holds **no** vector state on disk, and **node replication / self-healing are deliberately
     off** because Postgres is already the source of truth. Pushing a second derived copy onto
     the fleet would be a second write path and a second truth.

   The invariant that survives both: **one source of truth per deployment, and node replicas
   are only ever derived from it — never a second authority.** This is what phase 38 refuses to
   boot past: a node cannot run its own authoritative corpus (`LocalApi:Retrieval:Enabled`) while it
   is also meshed, because it would then hold a derived copy *and* an authority under the same
   collection names. See phase-38 D1 — that startup failure is this sentence, enforced.

   > **Phase 44 sharpened that sentence, and the sharper one is the invariant now: *one authority
   > per collection name, and the hub knows who it is.*** A node may hold an authoritative corpus
   > **only** where the hub assigned it and recorded the ownership
   > ([CollectionOwnership](src/InferHub.Coordinator/Vector/CollectionOwnership.cs)), which is what
   > lets replication and healing skip those names and a hub-side create of one be a `409`. The
   > phase-38 startup failure is unchanged — a node that sets `LocalApi:Retrieval:*` **by hand**
   > while meshed still refuses to boot. What changed is that "who owns this name" became a thing
   > the hub records rather than a thing it assumes. See phase-44 D1.
   Everything else stays in-memory;
   if you find yourself adding a database or on-disk format outside those directories/providers,
   stop and rethink. The default is `Enabled=false`, so deployments that don't opt in keep the
   original no-persistence contract unchanged.

   **Second recorded exception (phase 25): the usage ledger**, when `Usage:Persistence=postgres`.
   Deliberately its **own** connection string, not coupled to `VectorStore:Postgres` — a
   deployment may want durable usage without a Postgres vector store, or the reverse. The "one
   source of truth" rule is not violated because usage records are **append-only facts about work
   already done**, not a second copy of any live state. If the ledger ever starts being *read* to
   drive behaviour (routing, admission, anything), that reasoning has stopped being true and the
   design has drifted — stop. (Admission windows are fed in-memory by `UsageMeter`, never from
   the ledger.) Default is `none`: in-memory, reset on restart, like every other counter.

   > **Phase 47's image job registry is deliberately *not* a fourth exception, and the reason is
   > worth keeping.** It holds a job's record and its image bytes in memory, bounded by
   > `Images:Jobs:MaxRetainedBytes` and expiring after `Images:Jobs:RetentionSeconds` (five minutes),
   > and **nothing about it touches disk — no temp file, no spill under memory pressure, no cache
   > directory**. A restart forgets in-flight *and* completed jobs, like every other counter here,
   > and the docs say so unhedged. That is the whole test: the rule is about state that survives a
   > process, and this does not. If a future phase wants *durable* jobs it is a fourth exception and
   > must be argued **in this rule**, not added in the endpoint — because the moment a result
   > survives a restart, "where are my pictures kept" stops having the answer "nowhere, for five
   > minutes" and becomes a data-retention question somebody has to own.

   **Third recorded exception (phase 43): node profiles**, when `Fleet:Profiles:Persistence` is
   `file` or `postgres`. A profile that evaporates on hub restart is useless for the thing it was
   asked to do. The rule survives for phase-30 D2's reason: **a lost profile costs the fleet
   reverting to the operator-configured default on each box — never a wrong answer, and never a
   capability nobody granted**, because the node's own config remains the authority for what is
   *possible* and a profile is only a preference over it. See phase-43 D1/D3. Default is `none`.
5. **No new heavy dependencies.** The dependency surface is deliberately minimal (ASP.NET Core,
   SignalR, OllamaClient on the node, xunit for tests). There are exactly **two** recorded
   exceptions, both coordinator-only, both feature-scoped, both inert unless the feature is on:
   - **`Npgsql` + `Pgvector`** (phase 20) back the `postgres` vector provider. No connection is
     opened unless `VectorStore:Enabled=true` **and** `VectorStore:Provider=postgres`.
   - **`PdfPig`** (phase 23) backs PDF text extraction. It lives behind `IPdfTextExtractor`, is
     referenced by exactly one file
     ([PdfTextExtractor.cs](src/InferHub.Coordinator/Ingestion/PdfTextExtractor.cs)), and no code
     path reaches it unless a PDF is actually uploaded. Hand-rolling a PDF text-layer parser is a
     bad use of a week; taking a second-rate dependency into `InferHub.Shared` would be worse.

   Neither is in `InferHub.Shared` or `InferHub.Node`, and the rule still holds for everything
   else. Add packages reluctantly, and record them here when you do.

   **The `qdrant` vector provider (phase 33) added *no* dependency, on purpose.** Qdrant's official
   client is gRPC and would drag `Grpc.Net.Client` + protobuf into the coordinator; its REST API is
   plain JSON, and the house already speaks HTTP-to-a-server by hand
   ([OpenAiUpstreamClient](src/InferHub.Shared/OpenAi/OpenAiUpstreamClient.cs)). So
   [QdrantClient](src/InferHub.Coordinator/Vector/Qdrant/QdrantClient.cs) is a hand-rolled
   `HttpClient` connector. A third vector backend, still zero new packages — considered the
   dependency and declined it. Do not "upgrade" it to the gRPC client.

   > **PyTorch is not an exception to this rule, and the reason is the whole of phase-41 D1.**
   > The v3.14–3.19 image track put `torch`, `diffusers`, `transformers` and `bitsandbytes` into a
   > container — several gigabytes of native code, the heaviest thing this project has ever shipped
   > — and **zero of it is a `PackageReference`**. It is a `pip install` in one Dockerfile, in the
   > same category as phase-39's `curl` of an Ollama tarball, and it is reachable only as a **child
   > process over a line protocol**. Nothing in any `.csproj` compiles against it, nothing in C#
   > imports it, and a node that never starts the diffusion tool never loads a byte of it.
   > `BundledNodeTests.NoProjectReferencesPythonAndTheSharedProjectIsStillEmpty` is what keeps that
   > true. **If a future phase wants an image library in C# — to decode a mask, to check a raster,
   > to make a thumbnail — that is a new dependency and it needs an argument here**, not a
   > convenience in an endpoint. Phase 50 wanted exactly that and did not take it (50 D2).

   **Six phases, five new container images' worth of capability, and still two packages.** Phases
   46–51 added text-to-image, an async job model, a seven-model catalogue with quantization, 360°
   panoramas, editing and a console for all of it, at **zero** new `PackageReference`s.
   `InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.
6. **The node-facing *inference* job protocol is Ollama-shaped. Client-facing and upstream-facing
   dialects are translations at the boundary.**
   > **The word "inference" was added in phase 40, and it is a bounding, not a weakening.**
   > `InferenceJob` still carries Ollama JSON, always. What phase 40 settled is that work with no
   > Ollama shape to be — a transcription, a synthesis — travels as its own contract rather than as
   > an `InferenceJob.Kind` with a foreign body, which would have made this rule true of *some*
   > jobs and left every later reader to discover which. See phase-40 D3. `InferenceJob.RawJson` crossing SignalR and
   `InferenceChunk.ResponseJson` coming back are both Ollama JSON, always — that is the one
   shape the mesh's internals (dispatcher, router, affinity, retrieval) know. Request/response
   DTOs in `InferHub.Shared/Ollama/` track what real Ollama clients send; do not invent custom
   fields when Ollama already has one. Phase 21 added a second *client-facing* dialect and
   phase 22 a second *upstream-facing* one; both are translations at the edges, and neither
   changes what crosses the wire between coordinator and node.
7. **Conversations carry no content on the coordinator.** Clients re-send full history
   each turn; the coordinator stores only routing affinity keyed by either the
   `X-InferHub-Conversation` header or a hash of the opening message. **Phase 18
   inline retrieval preserves this**: the augmented request body is assembled
   in-flight inside the retrieval pipeline and forwarded to the node — nothing about
   the message or the retrieved context is retained on the coordinator.

   > **The rule has met three more kinds of content since, and each widened what "content" means
   > rather than what the rule permits.** Recorded here because a rule whose amendments live only
   > in their own phase sections is a rule the next reader will apply to the case it was written
   > for and no other.
   >
   > - **Audio is content** (phase-42 D5). A transcription request is a recording of somebody's
   >   voice and the answer is what they said. Nothing containing audio bytes or transcript text is
   >   logged at any level, and the line that *is* written carries the model, the duration and the
   >   outcome — **not the filename the caller chose**, which is metadata about somebody's day.
   > - **A prompt is content** (phase-46). A transcript is content because it is what somebody
   >   *said*; a prompt is content because it is what somebody *wanted*, and the picture is the
   >   answer — which makes an image request the most revealing thing a caller sends a fleet.
   >   Nothing logs one, on either host, at any level. What may be logged is the **recipe's trigger
   >   phrase** (phase-49 D2), because that is a constant of the model rather than the caller's
   >   words, and "why does this not look like a panorama" is otherwise undiagnosable.
   > - **An uploaded picture is content, and so is a mask** (phase-50). Both are held for the
   >   dispatch and dropped; both travel as `image` and `mask` rather than under the caller's own
   >   filename, for the phase-42 reason above.
   >
   > The mechanism is the same each time and is the thing to preserve: **count, never content**
   > (phase-25 D3). The usage path has gained four units — tokens, audio seconds, characters,
   > megapixel-steps — and **no field that could hold a sample**, deliberately, because a field is
   > an invitation. `ImagePrivacyTests` and `AudioPrivacyTests` both run a real request through a
   > real mesh with a capturing logger at `Trace` and fail if a known phrase appears anywhere in
   > the log or the ledger.

## Phase 52 (context you can load a slice of) — also load-bearing

**D1 — The split axis is the directory tree, because that is what the loader keys on.** This file
was 2 984 lines and roughly 64 000 tokens, loaded in full into every session before a question was
asked; 88% of it was `### Phase NN` decision blocks. **Considered and rejected: splitting by phase**
(`docs/decisions/phase-46.md` plus an index) — it is the tidier archive and it saves nothing,
because a reader working on the node does not know which phase numbers touched the node and would
load the index, guess, then load three files. The loader picking the right file up *automatically*
is the whole mechanism; a split it cannot see is a filing system, not a context strategy.

**D2 — A decision has one home and is pointed at from the others. Copying is forbidden.** This is
phase-38 D2's argument applied to prose: two copies of a decision are two copies that drift, and the
day they disagree the reader believes whichever their working directory happened to load. A pointer
cannot rot into a contradiction — only into a broken link, which is tested.

**The seven rules below stay here, whole**, because they bind every area and because amendments
attach to them — phase 51 found rule 5 and rule 7 each missing one.

**D3 — Four test projects, and `Mesh` is what justifies the other three.** It holds everything that
opens a socket or spawns a process. **Considered and rejected: one project with xUnit traits** —
`--filter` still builds the whole assembly, still recompiles on any edit, and still puts two agents
in one `.csproj`. The trait is a label on a monolith; the project boundary is what splits the build.
**It does not buy build isolation** — `Common` references both hosts — and claiming otherwise would
be a design fighting itself.

**D4 — Nothing under `src/` changed, and that was the phase's own acceptance test.** A phase that
reorganises tests is perfect cover for "while I was in there", and the day something breaks the
bisect has twelve hundred moved tests *and* a hundred touched source lines in it.

**D5 — The context is tested, because a broken pointer is invisible.** `ContextContractTests` holds
a checked-in inventory of every decision block that existed before the split and asserts each still
exists **exactly once**, that every pointer resolves, that the index and the files agree in both
directions, and that no file has grown back past its budget — **400 lines here, 1100 for a scoped
one.** A budget is the only thing that stops this being undone one paragraph at a time, which is
exactly how this file reached 2 984: nobody ever added more than a section.

**D6 — The saving is measured, not asserted.** See the release notes for the numbers.

## Auth model (three independent token sets)

| Scope | Config key | Guards |
|---|---|---|
| Inference clients | `Auth:ApiKeys` (anonymous) / `Auth:Clients` (named, with limits and an optional `Collections` scope) | `/api/generate`, `/api/chat`, `/api/tags`, etc. |
| Admins | `Auth:AdminApiKeys` | Everything under `/api/admin/*` (incl. `/usage`, `/clients`), **and `/metrics`** unless `Metrics:OpenScrape=true`. |
| Node enrollment | `Auth:NodeEnrollmentSecret` | The SignalR hub handshake. |

Tokens are hashed and compared with `CryptographicOperations.FixedTimeEquals` — keep that
pattern for any new key-checking middleware. The loopback exemption is shared (one
`RequireAuthForLoopback` flag covers both `Auth:ApiKeys` and `Auth:AdminApiKeys`).

`/health` is intentionally open so monitoring systems can poll it.

## Phase plans & release cadence

`plan/00-overview.md` indexes the per-phase briefs (`phase-09…12`). Each phase is one
mini-release with a strict shape:

1. Implement scope; keep tests green (`dotnet test`).
2. Bump `<Version>` in `Directory.Build.props` to match the phase's version.
3. Tag `vX.Y.Z` and write release notes (`.claude/release-notes-vX.Y.Z.md` is the local
   convention).
4. Flip the `Status:` line at the top of the phase file from `TODO` to
   `DONE ✓ (vX.Y.Z, YYYY-MM-DD)` and mirror the change in the overview table.

When asked to start a phase, read its plan file first — the scope, file list, and
acceptance criteria are already written.

## Writing a plan (the shape every file in `plan/` has)

**When asked for a new plan, write it in this shape without being told.** One phase →
`plan/phase-NN-short-slug.md`. A multi-phase track → one roadmap file,
`plan/roadmap-vX.Y-to-vX.Z-slug.md`, with a `Status:` line *per phase* inside it. Either way index it
in `plan/00-overview.md`. Plans are written for whoever implements them next with no memory of this
conversation, so a decision without its rejected alternative is a decision that gets undone.

**Header block** — title `# Phase NN — <the claim, in a sentence> (vX.Y.Z)`, then `Status: TODO`,
target version, **Size** (S/M/L + days), repo link, the file's own path, and a `>` callout naming the
prior `CLAUDE.md` decisions to read first (by number: "phase-36 D1/D3", not "the supervisor phase").

**§1 Goal** — what is true today and why it is not enough, in the repo's own words, with the file
paths. Then the shape of the change, with real commands or payloads a reader can run. Then
**Non-goals**, each written as *a decision with its reason*, never a bare list.

**§2 Design decisions** — `### D1 — <a full sentence that states the claim>`, numbered, each with:
the reasoning, the **alternative that was considered and rejected** and why, and — where it applies —
which rule (1–7) it brushes and what keeps the rule true. Mark the load-bearing one out loud ("this
is the decision the phase turns on"). The heading is the claim, so a reader skimming only headings
gets the design.

**§3 Tasks** — `- [ ]` checkboxes in dependency order, each naming a **real path** and what goes in
it. Order them so a failure is attributable: the thing that can break in isolation lands first.
Always include the CLAUDE.md block, the `appsettings.json` commented keys, README, and the
`plan/00-overview.md` row as tasks.

**§4 Acceptance criteria** — checkboxes, and they must include: *a deployment that changes no config
behaves identically to the previous version*, **zero new `PackageReference` / `InferHub.Shared.csproj`
still empty**, and `dotnet test` green. Anything that cannot be established from source says so and
points at §5.

**§5 Release ritual** — bump `<Version>` → `.claude/release-notes-vX.Y.Z.md` → tag → GitHub release
→ **pull the published image and run it** (an enumerated list of what to check on the target box —
this is not optional, see the D7 note in "Phase 21") → flip `Status:` + the overview row → static
site `inferhub.devart.solutions` (`#idocs_*` anchors, changelog row, "What's next") → blog post
(**slug, EN title, excerpt angle, draft-first**, `list_posts` before creating — the connector is
insert-only and the slug locks) → FB + X.

**§6** — appended *after* the release run: the verification results, with the observed numbers, the
exact host, and anything that did not run said out loud rather than omitted.

**A roadmap file adds**, before the phases: *Where we are* (shipped phases, current `<Version>`, the
seam being extended), *Overview* table (phase | version | theme | size | status), *What this
delivers*, *Why this order* (a paragraph per adjacency, arguing the dependency), *Invariants that
survive all phases*; and after them: *Sequencing notes* and *Deferred (tracked, not in this track)*.
Each phase inside it keeps §1–§5 as `###` subsections and is a **separate release** — separate tag,
notes, site edit, post. Do not batch them.

**House voice**: state the failure the decision prevents, concretely ("a client reads `error.message`
and gets a wall of backslashes"). Prefer a rejected alternative to an adjective. Never write a caveat
that a later phase makes false without deleting it everywhere — see the phase-35 note.

## Code style

- Records for DTOs; minimal-API delegates over controllers; primary constructors are
  used (e.g., [NodeHub.cs](src/InferHub.Coordinator/Hubs/NodeHub.cs)).
- File-scoped namespaces. No `using` statements at file top inside endpoint mapping
  extensions — collocate them.
- Comments are rare and explain *why*, not *what*. Match the existing tone.
