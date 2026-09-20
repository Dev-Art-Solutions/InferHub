# InferHub v3.44.0 — a tool manifest's `command` can be keyed by platform

Phase 79. Every shipped tool manifest has always had one POSIX absolute path in `command`, because
the only place a manifest had ever been written was the Linux-only `:tools` image. Investigating
"Windows tool workers" found the runtime itself was not the gap: `ProcessToolRuntime`,
`ToolWorkerPool` and `ToolWorkerProcess` are already plain `System.Diagnostics.Process`, no shell, no
POSIX-only APIs — `ToolWorkerProcess.ApplyEnvironment` already branches on the OS to add what Windows
needs to start a process at all. The gap was entirely in the manifest's own JSON.

## What's new

- **`command` may be an object keyed by platform** — `{"linux": [...], "windows": [...]}` — resolved
  against the node's own OS at load time, alongside the plain argv array every manifest has always
  used. One manifest, one id, both platforms; `Tools:Allowed` names it once.
- **`workdir` gets the same form**, with a deliberately different default: a missing platform branch
  is a refusal for `command` (a tool cannot start without an argv) and is simply "unset" for `workdir`
  (it always was optional).
- **A manifest naming only platforms this node does not run on is refused by name** — the same
  "loaded, logged, skipped" shape an unknown `Tools:Allowed` id already gets, so one tool that cannot
  start here does not take the node offline.
- **Proven with a real process, not only a parsed manifest.** The test suite starts the real
  `inferhub-echo-worker` — a native .NET executable needing no Python, no CUDA, no container — from a
  platform-keyed manifest, through the actual pool start/acquire/execute path, on whichever OS is
  running the tests.

## What was not established

- **No Windows branch was added to the shipped `whisper.json`/`piper.json`.** Doing so would be a
  real claim about a Windows Python venv, a CUDA-library-path equivalent, `ffmpeg` on the box — none
  of which this release built or ran. They are unchanged, POSIX-only, exactly as they were.
- **No Windows container image exists.** `:tools`/`:ollama`/`:diffusion` stay Linux-only bases; a
  `Dockerfile.windows` is a separate, larger undertaking (Windows containers, GPU passthrough under a
  different base image family) and nothing here is a step toward it beyond the manifest format.
- **Only the `windows` branch of the new resolution path was exercised by a live process start this
  session** — the box running the tests is Windows. The `linux` branch runs through the identical
  code path (the same `TryReadArgv`, keyed by a different string) and is covered by the parse-level
  tests, not by a second platform's live spawn.
- **No published image changed.** `python/manifests/*.json` are untouched, so there is nothing new to
  pull and run on a container this release.

## Also in this release

- **Phase 78's bookkeeping was stale and is corrected.** `v3.43.0` (the auto-scaler's scale-in
  direction) shipped — tagged, released, README and social copy done — but `plan/00-overview.md` and
  the phase's own brief still read `Status: TODO`. Both now read `DONE`, with phase 78's own two
  honest gaps (no live-fleet verification, no published-image check) carried forward in the row
  rather than erased by the status flip.

## Verification

`dotnet test tests/InferHub.Tests.Mesh` — **441 passed, 0 failed** — including the new
`ARealWorkerStartsFromAPlatformKeyedCommandOnThisMachine`, a real child-process spawn through
`ToolWorkerPool` from an object-form `command`.

**Published-image check:** not applicable — no image changed this release.
