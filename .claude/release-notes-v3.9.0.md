# InferHub v3.9.0 — the node can run your Python (as a subprocess, on purpose)

v3.8 taught the fleet to route on **what a node can do**, not just what it holds. That was the seam.
This is the thing behind it: a node can now start, supervise and talk to **child processes** that do
work its inference backend cannot — transcription, speech, OCR, whatever you write.

It is off by default, it adds **zero dependencies**, and it ships with **no tool**. The test suite
drives a real child process that echoes what it is sent, which is enough to prove the protocol, the
pool, the limits, the failure paths and the opt-ins. Whisper and Piper arrive in v3.10, onto
something already proven — landing them on an unproven process manager would mean debugging two new
things through each other, and every failure would look like a model problem.

```jsonc
// a manifest, in Tools:ManifestDirectory
{
  "id": "whisper",
  "capabilities": [ { "kind": "transcribe", "models": ["whisper-small"] } ],
  "command": ["/opt/inferhub/venv/bin/python", "-u", "/opt/inferhub/tools/whisper_worker.py"],
  "env": { "HF_HOME": "/data/tools/hf" },
  "maxWorkers": 1
}
```

```jsonc
// and the two consents on the node
"Tools": { "Enabled": true, "Allowed": [ "whisper" ] }
```

The node declares `transcribe`, the coordinator routes `(transcribe, whisper-small)` to it, and it is
reachable the same way on a hub and on a standalone node:

```bash
curl localhost:5080/api/tools/transcribe -H "Authorization: Bearer $KEY" \
  -F model=whisper-small -F file=@meeting.m4a
```

## Why a subprocess and not a library

Because the libraries are Python, and the obvious move — Python.NET, CSnakes, an embedded
interpreter — is a **native binding**. Three reasons it was declined, and the second is the one that
would have hurt:

- It pins the node to a CPython ABI and a specific interpreter build. `InferHub.Node` currently has
  two package references.
- **One bad `import` takes the node down.** A segfault in a native extension loaded into this
  process is not an exception you catch; it is a process that vanishes mid-stream, taking every
  in-flight inference job with it. A child process that segfaults is a log line and a restart.
- It forecloses the general case. A tool that is a Go binary, an `ffmpeg` invocation or a vendor's
  CLI works here for free and cannot work there at all.

So the node knows how to start a process, write a line, read a line and kill it. It does not know
what Python is. `python/README.md` is the worker author's document — the protocol, the manifest
reference, and exactly what happens when a worker misbehaves — and `python/inferhub_worker/` is a
~150-line reference implementation you copy or vendor. It is deliberately not a package.

## Opt in twice, because the second key is a ceiling

`Tools:Enabled` consents to the feature existing. `Tools:Allowed` names the manifest ids that may
actually run; a manifest on disk that is not in the list is **loaded, logged and never started**.

The two are not redundant, and the reason is a release away: **v3.11 lets a coordinator turn a
node's capabilities on and off, and `Tools:Allowed` is the ceiling it can never raise.** A single
switch would collapse "the operator enabled tools" and "the hub may run any tool present on this
box" into one consent — which is a coordinator compromise away from fleet-wide RCE.

Naming tools while `Tools:Enabled` is false **fails startup**, rather than doing nothing quietly:
reading your own list back and concluding it is on is the mistake worth catching.

## This is not a sandbox

Said plainly, because the alternative is implying safety by listing mitigations.

A worker runs **as the node's user, with the node's filesystem and the node's network.** What the
node does do is refuse to hand over its own environment: `ProcessStartInfo` normally pre-populates
the child's environment from the parent, so it is cleared and rebuilt — `PATH`, `HOME`, `LANG`,
`LC_ALL`, `TMPDIR`, `USER`, `SHELL`, whatever the manifest's `env` names, and nothing else.
`Coordinator__EnrollmentSecret` and `LocalApi__ApiKeys__0` do not reach a worker. That is a real
hole closed and it is the honest extent of the isolation.

**A tool you did not write and did not read has your box.** If you want real isolation, run the tool
in its own container and point a manifest at it — a "process" that is `docker exec` is still a
process, and the protocol does not care.

Two smaller containments come with it: `command` must be an **argv array** (a string is refused *by
name*, because every shell and every Docker `CMD` accepts one), and **nothing from a request ever
reaches the argv** — model, options and file paths all travel in the protocol, on stdin, after the
process is already running.

## A tool failure is a failed job, never a failed node

Every level has a deadline and a bound:

| | |
|---|---|
| Never finishes starting | Killed at `startTimeoutSeconds` |
| Overruns `requestTimeoutSeconds` | Killed; the **job** fails; the node keeps serving inference |
| Dies mid-request | Clean error; the next request starts a fresh worker |
| Fails to start 3× in 10 minutes | The pool gives up, logs once at Error, **withdraws its capabilities from the node's registration** — so the coordinator stops routing that work here — and keeps probing every minute, so a fix is noticed without a restart |
| Every worker busy | Waits `Tools:QueueMaxWaitSeconds`, then **503 + `Retry-After`**, the same shape as every other saturation refusal here |

The restart budget is lifted from the v3.4 Ollama supervisor rather than re-derived, and the
withdrawal reuses the mechanism a broken backend already uses — an empty declaration in the next
report is what unroutes a node. No new health field was invented.

The acceptance test for every row above is the same closing assertion: **the node still serves
inference afterwards.**

## Bytes go through files, not the pipe

A request's attachments are written to a per-request scratch directory and the frame carries a
*path*; the worker writes its output beside it and names it back. Base64 over stdio was considered
and declined: it is 4/3 the bytes and it materialises the whole payload as a string in *both*
runtimes at once — a 25 MB audio file becomes ~33 MB of .NET string plus ~33 MB of Python `str` plus
the decoded copies — for a handoff both sides' libraries would rather do with a path anyway.

The scratch directory is deleted in a `finally`: after success, and after every failure. Nothing a
request carried outlives it, and nothing containing it is logged. A worker that names an output file
*outside* its own scratch directory is refused and logged — that would turn "a tool ran" into "a
tool exfiltrated a file through the client-facing API".

Attachments are capped at 25 MB (`Tools:MaxAttachmentBytes`, matching what the OpenAI audio API
accepts), enforced at the edge as a `413` naming the limit, before anything is buffered onward.

## Solo mode gets it on the same day

`POST /api/tools/{capability}` works identically on a standalone node with no coordinator, because it
is the same executor with routing deleted — the same reason solo mode was cheap in v3.5 and solo RAG
was cheap in v3.6. A single container that transcribes is where this track is heading, and splitting
the local path across releases would have meant building it twice.

## Upgrading

**A deployment that changes no config behaves identically to v3.8.0.** With `Tools:Enabled` false —
the default — the node registers a no-op runtime, spawns nothing, reads no manifest directory and
declares no tool capability. There is no wire change for a fleet that is not using this.

Both node images now set `Tools__ScratchDirectory=/data/tools/scratch`, under the `chown app:app
/data` that has been there since v2.5.1. It is inert until you turn tools on.

- Zero new `PackageReference`; `InferHub.Shared.csproj` is still empty; no Python in any `.csproj`.
- `dotnet test`: **876 passed, 0 failed, 46 skipped.**

## What's next

**v3.10** — speech-to-text and text-to-speech for real: `faster-whisper` and `piper` behind
`/v1/audio/transcriptions` and `/v1/audio/speech`, on a third node image with the Python already in
it, metered in audio-seconds rather than tokens.
