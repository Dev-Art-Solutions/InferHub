# InferHub tool workers

A **tool worker** is a child process an InferHub node starts, supervises, talks to over a line
protocol, and restarts when it dies. It is how a node does work that is not chat and not
embeddings — transcription, speech, OCR, whatever you write next.

The node knows how to start a process, write a line, read a line, and kill it. **It knows nothing
about Python.** This directory exists because the libraries people want first (`faster-whisper`,
`piper`) are Python, not because the runtime is. A worker that is a Go binary, an `ffmpeg`
invocation or a vendor's CLI is exactly as valid, and the protocol below is all it has to speak.

> **This is not a package.** No `setup.py`, no PyPI release, no version to depend on. Copy
> `inferhub_worker/` next to your worker or vendor it. A published package is a support surface
> this repo has not agreed to maintain, and the whole library is ~150 lines.

## The 60-second version

`python/examples/echo.py` is a complete worker. With `inferhub_worker` beside it:

```python
from inferhub_worker import Worker

def handle(request):
    return {"echoed": request.payload}

Worker(capabilities=[{"kind": "echo", "models": ["echo"]}]).run(handle)
```

A manifest next to it in the node's `Tools:ManifestDirectory`:

```jsonc
{
  "id": "echo",
  "capabilities": [ { "kind": "echo", "models": ["echo"] } ],
  "command": ["/usr/bin/python3", "-u", "/opt/inferhub/tools/echo.py"],
  "workdir": "/opt/inferhub/tools",
  "env": { "HF_HOME": "/data/tools/hf" },
  "minWorkers": 0,
  "maxWorkers": 1,
  "startTimeoutSeconds": 120,
  "requestTimeoutSeconds": 600,
  "idleTimeoutSeconds": 900
}
```

And two keys on the node, because **running code is opted into twice**:

```jsonc
{
  "Tools": {
    "Enabled": true,        // the feature exists
    "Allowed": [ "echo" ]   // ...and this specific manifest may run
  }
}
```

Then:

```bash
curl http://localhost:5080/api/tools/echo -H 'Content-Type: application/json' \
  -d '{"model":"echo","hello":"world"}'
```

## The protocol

One JSON object per line, UTF-8, on the child's **stdin** (node → worker) and **stdout**
(worker → node).

| Frame | Direction | Meaning |
|---|---|---|
| `hello` | → worker | Sent once at spawn. Carries `protocol` and the manifest `tool` id. |
| `ready` | → node | Your reply. May carry `capabilities`. Past `startTimeoutSeconds` you are killed. |
| `request` | → worker | `id`, `capability`, `model`, `payload`, optional `files`, and `scratch`. |
| `chunk` | → node | A partial answer, correlated by `id`. Optional. |
| `result` | → node | Terminal. `payload`, and optional `files` you wrote into `scratch`. |
| `error` | → node | Terminal for this request; you stay alive. |
| `log` | → node | `level` + `message`, at any time. |
| `ping` / `pong` | both | Liveness probe for an idle worker. |

### Three rules

**1. stdout is the protocol.** One JSON object per line and nothing else. If a library prints to
stdout, redirect it — `contextlib.redirect_stdout(sys.stderr)`. The node ignores lines it cannot
parse rather than dying, but you will not see them where you expect.

**2. stderr is your log.** It is pumped into the node's log verbatim, tagged with your tool's id.
That is where a Python traceback goes, and a traceback is the most useful thing you can leave for
whoever is debugging you at 2am.

**3. Binary goes through files, never the pipe.** A request carries *paths* into a scratch
directory; write your outputs into `request.scratch` and name them back in the result. Base64 over
stdio would be 4/3 the bytes and would materialise a 25 MB audio file as ~33 MB of .NET string plus
~33 MB of Python `str` plus the decoded copies, for a handoff both sides' libraries would rather do
with a path. The node deletes the scratch directory when the request ends — after success and after
failure.

### `-u`, or: the hang that is not a hang

`"command": ["/usr/bin/python3", "-u", "worker.py"]`. Without `-u`, Python buffers stdout, the
`ready` frame sits in the buffer, and the node waits out `startTimeoutSeconds` and kills you. It is
the most common way a first worker fails and it looks exactly like a wedge.

## What the node guarantees, and what it does not

**It guarantees**: one request at a time per worker (`maxWorkers` is the concurrency, not
interleaving); a deadline on every level; a restart when you die; your stderr in the log; and that
your scratch directory is deleted.

**It does not sandbox you.** Say it plainly, because the alternative is implying safety by listing
mitigations: a worker runs as the node's user, with the node's filesystem and the node's network.
Dropping the environment (below) removes the most obvious credential leak and that is the honest
extent of it. **A tool you did not write and did not read has your box.** Run untrusted tools in
their own container and point a manifest at it over the network — a "process" that is `docker exec`
is still a process, and the protocol does not care.

### The environment you get

Not the node's. The node's environment holds `Auth__NodeEnrollmentSecret`, `LocalApi__ApiKeys__0`
and whatever else the deployment set, and handing all of it to a third-party script is a credential
leak wearing a convenience's clothes. A worker inherits:

`PATH`, `HOME`, `LANG`, `LC_ALL`, `TMPDIR`, `USER`, `SHELL` — plus, on Windows, the handful the
platform needs to start a process at all (`SystemRoot`, `COMSPEC`, `PATHEXT`, `TEMP`, …).

Everything else is dropped. If you need a variable, name it in the manifest's `env`.

### `command` is an argv array. There is no shell.

```jsonc
"command": ["/usr/bin/python3", "-u", "worker.py"]   // yes
"command": "/usr/bin/python3 -u worker.py"           // refused at load, by name
```

A command line assembled by concatenation is one quoting bug away from being an injection point,
and the values around it come from requests. **Nothing from a request ever reaches your argv** —
model, options and paths all arrive in the protocol, on stdin, after you are already running.

## Manifest reference

| Field | Default | Meaning |
|---|---|---|
| `id` | required | What `Tools:Allowed` names. Unique per directory. |
| `capabilities` | required | `[{ "kind": "...", "models": [...] }]`. The ceiling; `ready` may narrow it. |
| `command` | required | argv array. Element 0 is the program. |
| `workdir` | — | Working directory for the child. |
| `env` | `{}` | Extra environment, on top of the pass-through list above. |
| `minWorkers` | `0` | Started eagerly when the pool opens. |
| `maxWorkers` | `1` | Concurrency. **1 on purpose** — two copies of a model on one card is a memory error at the worst moment. |
| `startTimeoutSeconds` | `120` | `hello` → `ready`. Generous: loading weights is slow, not broken. |
| `requestTimeoutSeconds` | `600` | Per request. Overrunning kills the worker and fails the job. |
| `idleTimeoutSeconds` | `900` | Idle workers are retired, so a rarely-used tool does not hold VRAM forever. |

A manifest that fails to load is **logged and skipped**, never fatal: one bad JSON comma must not
take a node's inference offline.

## When a worker misbehaves

| What you did | What the node does |
|---|---|
| Never sent `ready` | Killed at `startTimeoutSeconds`; counted against the restart budget. |
| Overran `requestTimeoutSeconds` | Killed; the job fails; the node keeps serving inference. |
| Exited mid-request | Clean error, worker retired, next request starts a fresh one. |
| Failed to start 3× in 10 minutes | The pool gives up, logs once at Error, **withdraws your capabilities from the node's registration** so the coordinator stops routing that work here — and keeps probing every minute, so a fix is noticed without a restart. |
| Returned a file outside `scratch` | Refused and logged. That path is not read. |
| Returned files to a `stream=true` request | The job fails naming the limitation, rather than dropping them. |
