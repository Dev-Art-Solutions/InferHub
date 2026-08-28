# InferHub.Node/Tools — agent context

**Scope: `src/InferHub.Node/Tools/`.** The tool runtime — child processes over a line protocol —
and everything that rides it: STT, TTS, the image and video catalogues, the VRAM budget and the
licence gate.

> **Read the root `CLAUDE.md` first, then `src/InferHub.Node/CLAUDE.md`.** The rules that bind
> hardest here are **rule 5** (a `pip install` in a Dockerfile is not a `PackageReference`, and a
> native binding would be) and **rule 7** (a prompt, a recording and a picture are all content).

**Split out in phase 67**, for phase 62's reason one project over: `src/InferHub.Node/CLAUDE.md`
reached 1099 of its 1100-line budget, and the tool runtime and its media phases are the largest
coherent subtree the provider track had nothing to do with. The blocks below moved **unedited**;
`EveryPhaseDecisionBlockSurvivesTheSplitExactlyOnce` is the net under that move.

## Related context

- The node that composes this runtime: `src/InferHub.Node/CLAUDE.md`
- The workers it spawns: `python/CLAUDE.md`
- The envelopes and the job archive: `src/InferHub.Shared/CLAUDE.md`
- The hub that dispatches tool jobs: `src/InferHub.Coordinator/CLAUDE.md`
- The four node images: `deploy/CLAUDE.md`

## Decisions recorded here

### Phase 41 (the tool runtime) — also load-bearing

**D1 — The node speaks a *process protocol*, not Python. This is rule 5 in its strongest form, and
it is the decision the phase exists to get right.** The obvious move is Python.NET or CSnakes: call
`faster-whisper` in-process, no serialisation, no child to supervise. It was rejected on three
grounds, and the second is the one that would have hurt. It is a **native binding** — the heaviest
class of dependency there is — pinning `InferHub.Node`, a project whose dependency list is *two*
packages, to a CPython ABI. **One bad `import` takes the node down**: a segfault in a native
extension loaded into this process is not an exception you catch, it is a process that vanishes
mid-stream taking every in-flight inference job with it, whereas a child process that segfaults is a
log line and a restart. And it **forecloses the general case** — a tool that is a Go binary, an
`ffmpeg` invocation or a vendor's CLI is free here and impossible there. So
[ToolWorkerProcess](src/InferHub.Node/Tools/ToolWorkerProcess.cs) knows how to start a process,
write a line, read a line and kill it, and nothing in the node knows what language a worker is in.
`python/` exists because that is where the libraries are, not because the runtime is.

**D2 — Opt in twice, and the second key is a list rather than a boolean.** `Tools:Enabled` (default
**false**) consents to the feature; `Tools:Allowed` names the manifest ids that may start. A
manifest on disk that is not in the list is **loaded, logged and never run** — nothing is
discovered-and-executed, and `ToolSecurityTests` asserts the log line, because "I put the file there
and nothing happened" is otherwise a silent afternoon. This is phase-36 D6's shape (`Enabled` ≠
`AutoInstall`) with a sharper reason: **phase 43 lets a coordinator turn a node's capabilities on
and off, and `Tools:Allowed` is the ceiling it can never raise.** One boolean would collapse "the
operator enabled tools" and "the hub may run any tool present on this box" into a single consent,
which is a coordinator compromise away from fleet-wide RCE. The list *is* the grant, exactly as
phase-31 D1's `Collections` scope and phase-22 D5's `Fallback:ModelMap` are.

**D3 — The manifest declares capability, command and limits; `command` is an argv array and there is
no shell, ever.** A command line assembled by concatenation is one quoting bug away from being an
injection point, and the values around it (model names) come from requests — so
`ProcessStartInfo.ArgumentList` only, and **nothing from a request ever reaches the argv**: model,
options and paths all travel in the protocol, over stdin, after the process is running. A manifest
whose `command` is a *string* is refused **by name**, because every shell, CI config and Docker
`CMD` accepts one and a `JsonException` about a token type would never tell an operator which field.

**The child's environment is built, not inherited.** `ProcessStartInfo.Environment` is pre-populated
from this process, so it is **cleared** first and a short list added back (`PATH`, `HOME`, `LANG`,
`LC_ALL`, `TMPDIR`, `USER`, `SHELL`, plus what Windows needs to start a process at all) followed by
the manifest's `env`. The node's environment holds `Auth__NodeEnrollmentSecret`,
`LocalApi__ApiKeys__0` and whatever else the deployment set; handing all of it to a third-party
script is a credential leak wearing a convenience's clothes, and unlike most leaks it is invisible
because the script never has to *do* anything for the exposure to be real.
`ToolSecurityTests.AWorkerDoesNotInheritAVariableTheNodeHasAndTheManifestDidNotName` runs a real
child process and asks it; a stubbed `Process` would echo whatever the test author already believed.

**D4 — Workers are warm and pooled, not spawned per request, and `MaxWorkers` defaults to 1.**
`faster-whisper` spends seconds loading weights; per-request spawn would put that on every
transcription and thrash a card. The default of **1** is deliberate: a second Whisper process on the
same GPU is two copies of the weights and a memory error at the worst possible moment, so
parallelism is raised knowingly. Requests past the cap **wait** up to `Tools:QueueMaxWaitSeconds`
and then get **503 + `Retry-After`** — the same status and header as phase-25 D5's `RequestQueue`
and phase-37 D9's local gate, so a client's retry logic behaves identically whichever limit it hit.

**D5 — One JSON object per line, and bytes go through files rather than the pipe.** Frames are
`hello`/`ready`/`request`/`chunk`/`result`/`error`/`log`/`ping`/`pong` on stdin/stdout;
[ToolProtocol](src/InferHub.Shared/Contracts/ToolProtocol.cs) is the whole of it. **`stderr` is not
protocol** — it is pumped into the node's log under the tool's id, because that is where a Python
traceback goes and a traceback is the single most useful thing a tool author ever sees. Binary is
written to a per-request scratch directory and the frame carries a **path**; base64 over stdio was
rejected because it is 4/3 the bytes and materialises the payload as a string in *both* runtimes at
once (a 25 MB audio file → ~33 MB of .NET string + ~33 MB of Python `str` + the decoded copies), for
a handoff both sides' libraries would rather do with a path anyway.

The scratch directory is deleted in a `finally`, **always** — after success and after every failure
— and a worker that names an output file **outside** it is refused and logged rather than read: that
would turn "a tool ran" into "a tool exfiltrated a file through the client-facing API".

> **`Tools:ScratchDirectory` is the fifth instance of the container permissions trap** (phase-21 D7,
> the node id, phase-30 D3, phase-38 D4). The default stays relative so bare metal and Windows work,
> and **both** node Dockerfiles set `Tools__ScratchDirectory=/data/tools/scratch` under the existing
> `chown app:app /data`.

**D6 — A tool failure is a failed job, never a failed node, and never a hung one.** Every level has a
deadline and a bound: `startTimeoutSeconds` for `hello`→`ready`, `requestTimeoutSeconds` per request,
a `ping`/`pong` probe for idle workers, kill on timeout, and a restart budget with backoff **lifted
from `OllamaSupervisor` rather than re-derived** (3 attempts per 10 minutes, 10s doubling — phase-36
D4). Past the budget the pool **stops starting workers, logs once at Error, withdraws its
capabilities, and keeps probing** every `RecoveryProbeInterval`, so a tool that recovers is noticed
without a restart and one that does not cannot spin.

The withdrawal is the phase-36 D7 mechanism reused, not a health field invented: empty capabilities
in the next model report is what unroutes the node, and `IToolRuntime.CapabilitiesChanged` pushes
that report immediately the way `IBackendSupervisor.Recovered` does — otherwise the hub keeps routing
transcriptions at a node that stopped transcribing for up to `ModelRefreshInterval`.

**A worker that failed its request is terminated, not disposed politely.** The five-second grace on
`DisposeAsync` exists so a *cooperative* worker can close a half-written file; one that blew its
deadline is by definition not cooperating, and the pool's slot is released only once the process is
gone — so a polite wait is five seconds of the *next* caller's queue budget. Found by the test: the
follow-up request after a wedge failed until `TerminateAsync` existed.

**D7 — This is process isolation, not a sandbox, and the docs say so in those words.** A worker runs
as the node's user, with the node's filesystem and the node's network. Dropping the environment (D3)
removes the most obvious credential leak and that is the honest extent of it. **A tool you did not
write and did not read has your box.** Stating that plainly *is* the decision — the alternative,
implying safety by listing mitigations, is how somebody ends up running a random
`whisper-plus-telemetry.py` from a gist on a machine holding their fleet's enrollment secret. Real
isolation (a container per tool, seccomp, a user namespace) is deferred and named; the current answer
is to run untrusted tools in their own container and point a manifest at it, which the protocol
permits because a "process" that is `docker exec` is still a process.

**D8 — Solo mode gets tools on the same day, because it is the same executor.** Phase-37 D2's framing
a third time: the hub's endpoints are a formatting layer over the node's executor with routing
deleted. `ToolExecutor` is driven by `CoordinatorConnection` in a mesh and by `LocalApi/` in solo, and
neither knows about the other. A solo bundled node that transcribes with one `docker run` is where
this track is heading; splitting the local path across releases would mean building it twice.

*Recorded deviations from the phase brief, on purpose:*

- **A client-facing `POST /api/tools/{capability}` shipped on the hub too, not only in solo mode.**
  The brief named the solo route and left the mesh with dispatch but no way to invoke it, which is a
  tool runtime that is furniture — and the phase's own acceptance criterion says the echo worker
  round-trips "to a client". It is **generic on purpose** and phase 42's `/v1/audio/*` will sit
  *beside* it rather than replace it: an operator who writes their own tool needs a call InferHub did
  not have to know about in advance. It is under `/api`, which `BearerApiKeyMiddleware` already
  guards — *verified*, not assumed (phase-21 D2).
- **`ToolResult` carries `RetryAfterSeconds`.** Without it the edge would have to *sniff the error
  text* to choose between a 502 and a 503, which is precisely the inference phase-29 D6 refuses to
  make. The node states the fact; the edge renders it. It has a reader in both hosts today.
- **`IToolDispatcher` is a second interface on the same `Dispatcher`**, not four more methods on
  `IDispatcher`. Phase-34 D1's shape: one implementation, and nine existing test doubles are not made
  to fake methods they never call. Tool jobs go through the same job registry, the same in-flight
  accounting and the same `FailForConnection`.
- **A worker may *narrow* its manifest's capabilities at handshake and may never widen them.** A
  Whisper worker that finds one of the two model files it was promised should stop advertising the
  other; a script that could *add* capabilities to its own node would be deciding what traffic the
  fleet sends it. Same ceiling logic as D2.
- **The generic route refuses more than one returned file with a 501 naming the limitation**, rather
  than returning the first and dropping the rest — a lie with a 200 on it. One attachment is returned
  as bytes; none is returned as JSON.
- **`ToolEndpoints` asks the *capability declarations*, not the backend model list, whether a model
  exists at all.** A tools-only node reports zero Ollama models, so the phase-40 D5 "503 vs 404"
  split would have called every one of its models non-existent.

**Rule 5 survived again.** Phase 41 added **zero** new dependencies: `System.Diagnostics.Process` and
`System.Text.Json` ship in the shared framework, `InferHub.Shared.csproj` is still empty, and there is
no Python in any `.csproj` — the reference library in `python/` is copied or vendored, never packaged.

### Phase 42 (STT and TTS for real) — also load-bearing

**D1 — The client surface is OpenAI's audio API, exactly, and this is the phase-21 argument again.**
`POST /v1/audio/transcriptions` (multipart) and `POST /v1/audio/speech` (JSON). Every SDK in every
language already speaks it, so pointing an existing app at your own GPU is a base-URL change;
inventing `/api/tts` would be a second dialect whose only merit is that we designed it. Unlike chat
and embeddings there is **no Ollama dialect for audio**, so there is exactly one client shape and
the node-facing side is a `ToolJob` (phase-40 D3) rather than a translation.

**A worker always answers with the verbose shape — text, segments, duration — and the edge formats
every `response_format` out of it.** `srt` and `vtt` are string formatting on the hub
([TranscriptFormatter](src/InferHub.Shared/Audio/Transcript.cs)), phase-28 D1's Prometheus reasoning
applied to two subtitle formats that are forty lines between them. The alternative — telling the
worker which format to produce — would put SRT timestamp arithmetic inside every worker anybody ever
writes, in whatever language they wrote it in, and the day two workers disagreed about a comma the
bug would look like a model problem. `CultureInfo.InvariantCulture` on the timestamps is
load-bearing for the same reason it is in `PrometheusFormatter`: a decimal comma makes a WebVTT file
silently invalid on exactly the machines nobody runs CI on.

**A format that cannot be produced is a `400` naming the ones that can, never a substitution.** A
caller who asked for mp3 and got a wav has a corrupted file with a confident content type and finds
out in a media player three days later. The edge refuses an unknown value up front; a worker that
cannot encode (no `ffmpeg` on the box) answers with `ToolErrorCodes.UnsupportedFormat` and the edge
renders the 400 **from the code, never from the message** — phase-29 D6's refusal, and the same
shape as phase-41's `RetryAfterSeconds`. That is why `ToolFrame` grew a `code` field.

**D2 — `faster-whisper` for STT, `piper` for TTS, pinned, both CPU-viable.** Chosen for the reason
phase 39 shipped a CPU mode: most boxes that will run this have no spare card, and a TTS that needs
one is a TTS most of this project's users cannot run. Both are permissively licensed, self-hosted,
and phone nowhere. **Pinned by version rather than by hash**, which is a deliberate step down from
phase-39 D9's checksummed tarball and is argued in `python/requirements-tools.txt` at the pins: one
URL with an upstream sha256 is a different shape from a per-platform transitive closure, and a hash
list that is subtly wrong fails a build with something that reads like a network error, after which
the next person deletes the hashes.

**D3 — A third image, not a flag, and the other three are untouched.** `inferhub-node:tools` =
`:ollama` + a Python venv + the two workers (~6 GB). Phase-39 D2 verbatim: the wheels are in a layer
whether a flag is on or off, so a flag would grow every existing coordinator+node stack by ~1.5 GB
for a feature it does not use. `BundledNodeTests.NeitherOfTheOlderImagesLearnedAboutPython` fails if
that leaks, and `TheToolsImagePinsTheSameOllamaAsTheBundledOne` fails if the two images drift on
engine version. An operator on the plain image installs Python themselves and points a manifest at
it — the runtime does not care where the interpreter came from.

**D4 — Weights download on first use, behind a *third* opt-in.** `Tools:AllowModelDownload`, default
**false**, `true` in the `:tools` image. It is not redundant with the other two for the same reason
the second was not redundant with the first (phase-41 D2, phase-36 D6): `Enabled` consents to
running tools, `Allowed` consents to running *these* tools, and this consents to one of them
**reaching the internet from a box whose operator may have deliberately air-gapped it** — the reach
phase-39 D7 refused to do at boot. Choosing the `:tools` image *is* that consent, exactly as
choosing `:ollama` is the consent to run an Ollama. With it off, a worker that needs missing weights
fails the **job** naming the key and the exact pre-fetch command, and the node keeps serving
everything else. The cache is under `/data`, on the volume, so it happens once rather than once per
`docker run`.

The flag reaches the worker as `INFERHUB_ALLOW_MODEL_DOWNLOAD`, **stated** into the child's
environment rather than inherited — which is the only way it could, since phase-41 D3 clears that
environment first. `ToolSecurityTests` drives a real child process and asks it.

**Voices are not fetched at all.** There is no default voice that is right for everyone, and a
confident answer in the wrong language is worse than a refusal.

**D5 — Audio is content, and none of it is kept.** Rule 7 in its most literal form yet: a
transcription request is a recording of somebody's voice and the result is what they said. The hub
buffers the upload for the dispatch and drops it — no temp file, no cache; the node writes it into
the per-request scratch directory deleted in a `finally` (phase-41 D5); **nothing containing audio
bytes or transcript text is logged at any level**, and the line that *is* written carries the model,
the duration and the outcome — not the filename the caller chose, which is metadata about somebody's
day. `AudioPrivacyTests` runs a transcription through a real mesh with a capturing logger at `Trace`
and fails if a known phrase from the fixture appears anywhere in the log or the ledger — the harder
version of `UsageLedgerTests.NoPromptOrCompletionTextExistsAnywhereInTheUsagePath`, which asks
whether a field exists rather than whether a phrase leaked.

**D6 — Concurrency is the tool's, not the fleet's.** `maxWorkers` defaults to 1 (phase-41 D4), so a
node transcribes one file at a time unless an operator raises it knowingly. Because routing is per
`(capability, model)` since phase 40, **a node busy transcribing is still a candidate for chat** —
"my chat got slow when someone uploaded a podcast" is the failure phase 40 landing first prevents,
and it is worth saying in the release notes because nobody can see it working.

**D7 — Usage is metered in the unit the work is actually in.** `UsageRecord` grew `Units` (a double)
and `UnitKind` (`tokens` | `audio_seconds` | `characters`), appended with defaults that describe
today's rows, so every existing consumer and every row already in a Postgres ledger keeps meaning
what it meant. Transcription meters **audio seconds** measured off the decoded file by the worker
(not derived from the upload's byte count, which a variable-bitrate encoding would make a guess);
speech meters **input characters**, counted at the edge because the edge already knows and should
not have to trust a third-party script for a number that appears on somebody's bill.

Phase-25 D3 is unchanged and is why this is safe: these are counts computed from what was processed,
and there is deliberately no field that could hold a sample. Client limits gained
`AudioSecondsPerDay` and `CharactersPerDay` — **separate budgets, each consuming only its own unit**,
because a client whose only limit is `TokensPerDay` could otherwise transcribe a library for free.

> **The Postgres migration is additive and must stay that way.** `ADD COLUMN … DEFAULT` has not
> rewritten a table since PostgreSQL 11, so a ledger with two years of chat in it gains two columns
> in milliseconds — and it runs through `ConcurrentDdl` because two hubs may boot together
> (phase-32 D7). Old rows get `units = 0, unit_kind = 'tokens'`, which is why `UsageAggregate` reads
> the **token columns** for tokens and `units` only for the two new kinds. `UsageAggregate` also
> gained two separate columns rather than one `units` sum: a client that chatted and transcribed has
> rows in two units under one model grouping, and a single sum would add seconds to tokens and
> produce a number wrong in a way no reader can detect.

*Recorded deviations from the phase brief, on purpose:*

- **The worker error `code` field is new machinery the brief did not name.** The brief asked for a
  400 on an unproducible format without saying how the edge would know, and the only alternatives
  were sniffing the error text (phase-29 D6 refuses it) or hard-coding the format matrix on the hub
  (which would be wrong the day a worker gains `ffmpeg`). The node states the kind; the edge renders
  it. Deliberately a very short list — a code nobody renders is a code that is wrong by the time
  somebody reads it.
- **A manifest capability with an empty `models` list is an open set** — the one widening anywhere
  in the tool runtime, and it is bounded: the **kind** is still the manifest's to grant, and every
  name a worker reports for it corresponds to a file the operator put on the box. Piper's models are
  voice files dropped into a directory, and no list written in advance survives the first new voice
  — the drift phase-40 D2 refuses for backend models. `models` *omitted* is still a mistake, so the
  two are distinguished by null-versus-empty rather than collapsed.
- **`/v1/audio/*` sits beside `/api/tools/{capability}`, not over it.** The generic route stays for
  the operator who writes their own tool, exactly as phase 41's deviation note promised.
- **The requirements are version-pinned, not hash-pinned.** See D2.
- **No `/v1/audio/translations`.** One flag on the same worker; shipping an untested surface to look
  complete is how a feature list starts lying.

> **v3.10.0 was dead on arrival, in three separate ways, and v3.10.1 fixed it the same night.**
> The fifth time (v2.5.1, v3.0.1, v3.5.1, phase-32 D7) — every one found by pulling the published
> image, none by a test. The three that are worth carrying forward as rules:
>
> 1. **SignalR's default `MaximumReceiveMessageSize` is 32 KB, and exceeding it kills the
>    connection rather than failing the message.** Every real `/v1/audio/speech` through a
>    coordinator was a 500 that also dropped the node. Phase 41 had verified attachments across a
>    real wire — with a **16-byte** file, four orders of magnitude under the cap, which proved the
>    plumbing and nothing about the size.
>    [NodeHubLimits](src/InferHub.Coordinator/Hubs/NodeHubLimits.cs) now *derives* the wire cap from
>    `Tools:MaxAttachmentBytes` (base64 is 4/3, plus an envelope), because two numbers that have to
>    agree are two numbers that will not. **When a phase adds bytes to the wire, test a payload past
>    32 KB or the wire test is decoration.**
> 2. **The interpreter that builds a venv must be the interpreter that runs it.** The venv was built
>    in a `debian:trixie-slim` stage (Python 3.13) and copied into the `aspnet:10.0` runtime (3.12);
>    site-packages live under `lib/python3.13/` and nothing was importable. Everything *looked*
>    right — manifests loaded, `/api/status` answered — and the first transcription would have died
>    on an `import`. It is now built in the final stage, and `Dockerfile.tools` **asserts the import
>    at build time**, which is the only reason this cannot ship again.
> 3. **An open model set has to start a worker eagerly, or it deadlocks.** Nothing declares the
>    capability until a worker reports; no worker starts until a request is routed; nothing routes
>    to an undeclared capability. A TTS node with a voice on its volume refused `speak` forever.
>
> And one that is a design lesson rather than a bug: **a `libcuda` the driver injects is not a CUDA
> runtime.** CTranslate2 needs `libcublas`/`libcudart`, which phase-39 D1 got for free because
> Ollama ships its own — they were already in the image, just off the worker's loader path. The
> Whisper manifest's `env` points `LD_LIBRARY_PATH` at them, and the worker now **falls back to the
> CPU loudly** rather than failing the job: phase-39 D6's line, that a card which cannot be used is
> the operator's problem while a failed job is everyone's.

> **A validator written for a hand-edited file is wrong for an image.** `Tools:Allowed` refused a
> blank entry, on the good reasoning that a blank id hides a typo behind an index. But an array that
> arrives from an image's environment cannot have an element *removed* — `-e Tools__Allowed__1=` is
> the only lever `docker run` gives you — so the `:tools` image could not be run with one tool, or
> with none: `-e Tools__Enabled=false` failed startup and no second flag helped. Blanks are ignored
> now. Nothing is hidden by it: a manifest not in the list is still loaded and still logged **by
> name** as not started, which is the signal the strict check was standing in for.

**Rule 5 survived again.** Phase 42 added **zero** new `PackageReference`s, `InferHub.Shared.csproj`
is still empty, and there is no Python in any `.csproj` — the Python is a `pip install` in one
Dockerfile, which is the same category as phase-39's `curl`.
`BundledNodeTests.NoProjectReferencesPythonAndTheSharedProjectIsStillEmpty` asserts both.

### Phase 48 (the catalogue: six models, quantized, budgeted) — also load-bearing

**Four more recipes, and every one of them needed something phase 46 did not have.**
`flux-schnell` (12B) and `qwen-image` (20B + an 8.3B text encoder) **do not fit a 24 GB card at
bf16** — 33 GB and 60 GB — so they exist only because of nf4. `sd35-medium` and `sdxl-turbo` fit
fine and need a *licence decision that is not ours to make*.

**D1 — The VRAM budget is declared, not detected, and the worker's reading is a cross-check.**
`Node:Vram:BudgetMiB` is a number the operator sets and `Node:Vram:ReserveMiB` (2048) is what is
held back for the inference backend and the display. **Considered and rejected: detecting VRAM and
defaulting the budget to it.** It works on bare-metal Linux, is wrong under WSL2 — where this
project's own GPU box lives, and where there are no `/dev/nvidia*` device nodes, the host's
`nvidia-smi` cannot see the VM's VRAM, and the only reliable signal a GPU exists is that
`libcuda.so.1` loads (phase-39 D5) — is wrong on a shared card, and is wrong the moment somebody
else's process is on the GPU. **A budget that is usually right is worse than one that is explicitly
absent**, because the first failure is an OOM inside somebody's job rather than a startup message.
The worker reports `torch.cuda.mem_get_info()` on its `ready` frame purely so `ToolWorkerPool`
can **log a disagreement** past a 10% band; nothing routes, budgets or admits on it. Unset (0) means
no gate and v3.15's behaviour exactly.

**D2 — The budget is an admission gate on the node, before the job starts — and it is consulted
*after* the worker slot is taken.** That ordering is the trick: only then is "what is in flight" a
fact rather than a guess. [VramBudget](src/InferHub.Node/Tools/VramBudget.cs) is **pure**
(`budget, reserve, residents, candidate → admit | wait | refuse`) for `NodeProfileClamp`'s reason —
it is the piece whose off-by-one costs somebody an OOM at 2am, and a pure function is the piece a
test can pin exhaustively. **Only what is *in use* counts against a candidate**: an idle pipeline is
freed by the worker *before* it allocates the next one, so the peak is never both models at once,
and a model somebody is mid-job on is never evicted — over the budget the request **waits** on the
existing tool queue and then gets `503` + `Retry-After`, the same status and header as every other
limit here. `Refuse` and `Wait` must not collapse: one is "come back shortly", the other is "this
box will never run that", and the second is also why such a recipe is **never declared** (41 D6's
withdraw-on-failure, applied before the first failure).

> **`ImageResidency` mirrors the worker's own LRU policy rather than measuring anything**, so the
> two agree without a round trip. Where they can differ is a load that *fails*: the node then
> believes the new model is resident when nothing is, which errs toward **refusing** work rather
> than toward an OOM. That asymmetry is the right one. An idle hint clears only the idle entries —
> anything a lease still covers stays, or the gate would admit a second model onto a busy card.

**D3 — Switching recipes swaps weights inside a warm worker; it does not restart it.** Loading FLUX
is 40–90 s and a restart pays the interpreter and the import of torch on top of that, on every
alternation. `Tools:Image:ResidentRecipes` (default **1**) allows more than one resident where the
budget permits — the default is 1 for phase-41 D4's reason, that the expensive default is the one
nobody realises they chose. **Idle unloading is the worker's decision, not the node's**: the node
sends an `idle` hint frame after `idleTimeoutSeconds` and the worker frees its VRAM and stays alive.
A node-side unload would be the node reaching into a tool's internals (41 D1).

> **`ToolWorkerPool.WorkerFloor` is a bug fix as much as a feature.** An open model set forces an
> eager worker because nothing declares such a capability until a worker reports (the v3.10.0
> deadlock) — and until v3.16 the maintenance pass would happily **retire that very worker** after
> `idleTimeoutSeconds`, leaving a pool that still declares models with no process able to re-declare
> when one lands, and killing a prefetch in flight. The last worker of an open-set pool is now kept
> and hinted instead. Hinted **once** per idle period, not every tick.

**D4 — Weights are pulled by an explicit command, never lazily inside a request.** FLUX is ~24 GB on
the wire and Qwen-Image is larger; a lazy first-use download blows `requestTimeoutSeconds`
(v3.14.0 shipped exactly that and every first `sdxl` call was a 502 after 899.99 s), and raising the
timeout to cover it means every genuinely wedged job also takes forty minutes to fail. So phase 26's
model-command channel is extended: `ModelCommand` gains a nullable `Tool` — **null means the
inference backend**, which is every command that existed before v3.16 — and
`POST /api/admin/nodes/{id}/tools/{tool}/models/{recipe}/pull` sends it down the node's own outbound
connection, with progress relayed on the existing `/api/admin/stream`. No new transport; the
coalescing, the reused-command-id behaviour and the "no persistent state" property all come with it.
**`warm` is refused for a tool model** rather than given an invented meaning — residency is already
decided by `ResidentRecipes` and the idle hint, and a third opinion is a third thing to be wrong.

**The progress carries no percentage, deliberately.** `huggingface_hub` gives no download callback,
and a denominator a worker would have to guess is a number a dashboard would happily plot
(phase-28 D5). It reports how many mebibytes have landed instead.

> `IToolRuntime.AcquireToolAsync` is **the one path that does not go through a capability**, and it
> has to be: a pull exists precisely because the model is not there, so it is not declared, so
> `AcquireAsync` would answer "this node does not provide it" — correctly, and uselessly. The
> ceiling is intact, because what is addressed is the *tool*, which `Tools:Allowed` named. It takes
> an ordinary worker lease, so a pull queues behind a generation and vice versa: a node does not
> quietly grow a second lane to the GPU.

**D5 — A non-permissive licence needs a fourth opt-in, named per model.** A recipe with
`license.permissive != true` is **loaded, logged by name and not started** unless its licence id is
in `Tools:Image:AcceptedLicenses`, with the log line naming the licence and linking to it. It is not
redundant with the other three (41 D2, 42 D4): `Enabled` is the feature, `Allowed` is *these tools*,
`AllowModelDownload` is reaching the internet, and none of them says "and I accept the Stability AI
Non-Commercial Research Community License". **A list, not a boolean** — `sd35-medium` is free for
most people who will run it and `sdxl-turbo` is not usable commercially at all, so one flag would let
somebody who read one licence enable both. **A recipe that says nothing is treated as *not*
permissive**: one that forgot to say is one nobody has read the licence of, and the other default
would make the consent opt-out by accident of a missing field. Enforced **twice** — the node refuses
to declare it (so the hub never routes at one) and the worker refuses to download or load it (the
lock on the process that would actually do those things, which a solo caller meets directly).

> *Recorded deviation from the brief:* the key names **licence ids**, not recipe ids. What is being
> accepted is a licence — you read it once and it covers every model under it — and the key's name
> says so. Both shipped non-permissive recipes have distinct licence ids, so the two readings behave
> identically for this catalogue; the refusal prints the exact string to add and a link to the text.

**D6 — Quantization is a recipe field with three values and a stated cost**, and what this box needs
from it is one number: `vramMiB` is the **quantized** figure the gate admits against.
**The argument moved to `python/CLAUDE.md` in phase 58** (52 D2, and 58 is the phase that revisited
it): it is about what a *recipe* is, not about what a node budgets.

**The node reads recipe files, and that is not the node learning about diffusion.**
[ImageRecipeCatalogue](src/InferHub.Node/Tools/ImageRecipeCatalogue.cs) parses exactly three things —
id, licence, VRAM — and never `repo`, `pipeline`, `variant`, `dtype` or the aspect buckets. Two
consumers need the answer with **no worker running**: `NodeProfileClamp` is pure and must refuse an
oversized or unlicensed recipe synchronously, and the decision not to *fetch* an unlicensed model has
to precede the process that would fetch it. What the node learns is licences and megabytes, which are
facts about the box. A recipe with no `revision` is skipped by name here exactly as it is in the
worker — a catalogue that counted a model the worker will never offer would budget VRAM for something
that cannot run.

**Profiles gain `imageRecipes`, the third thing a hub can narrow and the first whose ceiling is
arithmetic.** `false` narrows and always works; `true` is honoured only for a recipe the box has,
has accepted the licence of, and has the VRAM for — refused otherwise with the numbers in the
message. 43 D1 is unchanged: the hub cannot make a node accept a licence, find weights or grow a
card. A narrowed recipe **stops being declared**; the pool keeps running, so switching `sdxl-turbo`
off does not take `sdxl` down with it (phase-43 D6's in-place shape).

**Rule 5 survived again** — zero new `PackageReference`; `bitsandbytes` is a line in
`requirements-diffusion.txt` and the argument for *when* it arrived is in `python/CLAUDE.md`.

### Phase 55 (seam repair) — the pointer, and the one node-side fact

The decisions are in `python/CLAUDE.md` (phase 55). What is *this* project's is one key:
**`Tools:Image:SeamRepair`** (`off` default | `blend` | `diffuse` | `any`), validated at startup
against those four words and **stated into the worker's environment** as
`INFERHUB_IMAGE_SEAM_REPAIR` — the only way it could reach the child, since 41 D3 clears that
environment first. It is a ceiling in exactly `Tools:Allowed`'s sense (41 D2), one level down: the
caller names a mechanism on a header, and this decides whether they may have it.

**Nothing on the node parses a request payload to enforce it, and that is deliberate** — see the
deviation recorded with those decisions. `ImageRecipeCatalogue` reads three fields out of *recipe
files*; the runtime has never seen a request body, and teaching it to read diffusion requests to
enforce a key the worker can enforce itself would be the node learning about diffusion (41 D1). It is
48 D5's shape with the redundant half removed: the node states the grant, the worker refuses
**naming the key**, and a solo caller meets the same refusal in the same words.

### Phase 56 (durable image jobs) — the pointer

The decisions are in `src/InferHub.Shared/CLAUDE.md` and the exception is argued in **rule 4** in the
root file. What is this project's is one line of composition: the node builds the archive from the
same key through the same `ImageJobArchives.Create`, so a solo node's jobs survive a restart exactly
as a hub's do — 41 D8's pattern for the fifth time. `Images__Jobs__DataDirectory=/data/images` is set
in all four node images: the container permissions trap, seventh instance.

### Phase 57 (the video seam) and 58 (the catalogue) — the pointers, and the node-side facts

The decisions are in `src/InferHub.Shared/CLAUDE.md` (57 D1–D4) and `python/CLAUDE.md` (57, 58).
What is *this* host's is one predicate, one deliberate omission, and one default reversed for video.

**`CapabilityKinds.IsGenerativeMedia` replaced `IsImageKind` at the three places
`ProcessToolRuntime` reasons about a *recipe*** — the declaration narrowing, the VRAM budget taken
after the worker slot, and the licence-and-budget refusal. 50 D1's sentence one kind on: a node
gating only the image kinds would happily render video with weights whose licence nobody accepted.

**`NodeToolState.Images` carries video recipes since v3.27**, with the recipe's `media` on each row
— 57 kept them out so clips could not land in a panel that draws pictures, and 59 D1 moved that
split to the console instead. **Solo got the surface on the same day** (41 D8):
`LocalVideoEndpoints` maps the same four routes and the same two `501`s.

**Phase 58 gave the catalogue a fourth field, `media`, and flipped one default for video only.**
48 D2's *a recipe with no declared figure is admitted rather than guessed at* keeps a number nobody
wrote down from refusing a model the operator can see on the box, and it is right where the miss is
4–8 GB. The same silence admits a 24 GB model onto a 12 GB card as an out-of-memory error four
minutes into somebody's job. So a **video** recipe with no `vramMiB` is not declared; an image recipe
with none behaves exactly as it did in v3.25. **Considered and rejected: requiring it of every
recipe** — tidier, and it silently stops declaring an operator's hand-written `sd15` clone on
upgrade. Reading `media` is still not the node learning about diffusion (41 D1): nothing here reads
`fps`, `durations`, `repo` or `pipeline`, and the field buys **which recipes must state their
megabytes**. The clock is the worker's gate, one process down.

**`VramBudget.Fits` withholds a shipped recipe for the first time**: `wan-t2v-14b-720p` declares
24 000 MiB against a 24 GB card's 22 528 of headroom, so such a node never declares it. It has
existed since 48 and never fired, so `RecipeCatalogueTests` names the exception rather than
asserting everything fits.

### Phase 70 (streamed speech) — the node-side half, and the one that could take a connection down

The wire format, the encoder and D1/D3–D6 are in `src/InferHub.Shared/CLAUDE.md`. What is this
directory's is **D2**, and it is the load-bearing one.

`ToolProtocol.MaxChunkPayloadBytes` is 30 KiB and `ToolExecutor.StreamAsync` enforces it on every
`chunk` frame it forwards, modality-blind. **The failure it prevents is not a failed request.**
SignalR's default `MaximumReceiveMessageSize` is 32 KB and exceeding it kills the *connection*, not
the message — which is how phase 42 found that a 300 KB wav dropped the node and made it re-register
(see `NodeHubLimits`). A blocking answer crosses the wire once; a streaming one crosses it fifty
times, so the same mistake is fifty times likelier to be made by somebody's worker. The number is
deliberately under SignalR's own default rather than derived from `Tools:MaxAttachmentBytes`: the
node cannot see the hub's configuration, and a limit that is only correct on a generously configured
hub is a limit that fails where it matters.

**The worker is retired when it happens**, which is the opposite of what cancel does (47 D3) and for
a reason cancel does not have: the stream is abandoned *without the worker being told*, so its
remaining frames sit on the pipe against a request that no longer exists — and a warm worker would
hand them to the next caller, as their answer. A weight load is the cheaper mistake.
`AnOversizedChunkFailsTheJobAndTheConnectionSurvivesIt` asserts both halves, and the second half is
the point: it asks the same mesh for something else afterwards, which is the only way to tell a
failed job from a killed connection.

**Nothing here parses audio.** A speech chunk is a payload string the node measures and forwards;
what is in it is `SpeechStream`'s business at the edge — 55's deviation, unchanged.
