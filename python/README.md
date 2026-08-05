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

One variable the **node** states into your environment rather than passing through:
`INFERHUB_ALLOW_MODEL_DOWNLOAD` (`1` or `0`, from `Tools:AllowModelDownload`, v3.10+). If your
worker fetches weights on first use, check it — and when it is `0`, fail the request with a message
naming the flag *and the exact pre-fetch command*, because an operator who is told only "download is
off" has to go and find out what the command was.

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
| `capabilities` | required | `[{ "kind": "...", "models": [...] }]`. The ceiling; `ready` may narrow it. **`"models": []` is an open set** (v3.10+) — the kind is still granted here, and the worker's `ready` decides the names. Omitting `models` is still a refusal. |
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

### Saying whose fault it was (v3.10+)

By default a worker's `error` frame becomes a **502** — the caller reads it as "the server is
broken". Sometimes that is a lie: a box with no `ffmpeg` asked for an mp3 has a *request* problem,
and the caller can fix it by asking for a wav. So an `error` frame may carry a `code`:

```python
from inferhub_worker import ToolError, ERROR_UNSUPPORTED_FORMAT

raise ToolError("this worker cannot produce 'mp3'. It can produce: wav, pcm",
                ERROR_UNSUPPORTED_FORMAT)
```

| Code | The edge renders |
|---|---|
| `invalid_request` | `400` |
| `unsupported_format` | `400` — and **your message must name what you *can* do** |
| `model_unavailable` | `502` — nothing the caller sends fixes it; name the fix for whoever runs the box |
| anything else, or none | `502` |

The code exists so the edge never reads your error *text* to pick a status. Keep the list short: a
code nobody renders is a code that is wrong by the time somebody reads it.

## The two shipped workers (v3.10+)

`tools/whisper_worker.py` and `tools/piper_worker.py`, with `manifests/whisper.json` and
`manifests/piper.json`, are what the `ghcr.io/dev-art-solutions/inferhub-node:tools` image runs.
They are also the worked examples: between them they use every part of the protocol — file in, file
out, capability narrowing at handshake, a structured refusal with a code, and stderr as the log.

```bash
# on a plain node, or bare metal — the runtime does not care where the interpreter came from
python -m venv /opt/inferhub/venv
/opt/inferhub/venv/bin/pip install -r requirements-tools.txt
cp -r inferhub_worker tools manifests /opt/inferhub/
```

Then `Tools:Enabled=true`, `Tools:Allowed=["whisper","piper"]`,
`Tools:ManifestDirectory=/opt/inferhub/manifests`, and — if you want Whisper to fetch its own
weights — `Tools:AllowModelDownload=true`.

**They answer `/v1/audio/transcriptions` and `/v1/audio/speech`**, on a hub and on a solo node
alike. The worker never sees `response_format` for transcription: it always returns
`{text, language, duration, segments}` and the edge produces `json`/`text`/`srt`/`vtt`/
`verbose_json` from it, so no worker author writes a subtitle timestamp.

### `whisper_worker.py`

Models are `whisper-tiny` … `whisper-large-v3-turbo`. **It narrows at handshake**: with
`INFERHUB_ALLOW_MODEL_DOWNLOAD=0` it offers only the sizes already in `HF_HOME`, so the fleet never
routes a job at a box that would have to reach the internet to answer it. Device is CUDA when
`ctranslate2` can see one and CPU otherwise, **and it logs which** — four gigabytes of CUDA runtime,
a dropped `--gpus` flag and a silent CPU fallback is an afternoon spent blaming the model.

### `piper_worker.py`

Voices are `.onnx` + `.onnx.json` pairs under `INFERHUB_PIPER_VOICES`; the model name is the file's
stem. Its manifest declares `"models": []`, which is an **open set**: the worker reports what it
found. That is the one place a worker's report may add a model rather than only remove one, and it
is bounded — the manifest still decides that this tool may `speak` at all, and every name reported
is a file the operator put on the box. There is no list anybody could write in advance that would
survive the first new voice.

`wav` and `pcm` are native; `mp3`, `opus` and `flac` need `ffmpeg` on the box, and without it the
worker refuses with `unsupported_format` naming what it can do rather than handing back a wav with
an mp3's content type.

## The diffusion worker (v3.14+)

`tools/diffusion_worker.py` with `manifests/diffusion.json` is what
`ghcr.io/dev-art-solutions/inferhub-node:diffusion` runs. It answers `/v1/images/generations` on a
hub and on a solo node alike, and it is the third worked example — the one that shows a **recipe
catalogue** rather than a fixed model list.

```bash
python -m venv /opt/inferhub/venv
/opt/inferhub/venv/bin/pip install -r requirements-diffusion.txt
cp -r inferhub_worker tools recipes manifests /opt/inferhub/
```

Then `Tools:Enabled=true`, `Tools:Allowed=["diffusion"]`,
`Tools:Image:RecipeDirectory=/opt/inferhub/recipes`, and `Tools:AllowModelDownload=true` if you want
it to fetch its own weights.

**It is a separate requirements file, and a separate image, on purpose.** The torch CUDA wheels are
several gigabytes; stacking them onto `:tools` would put PyTorch into every audio deployment and
Whisper into every image one. See `recipes/README.md` for the recipe format, and the header of
`Dockerfile.diffusion` for why the image has no Ollama in it.

### Recipes, not models

The manifest declares `"models": []` — the same **open set** `piper.json` uses, for the same reason
and with the same bound: the manifest decides that this tool may serve `image` at all, and every
name the worker reports is a recipe file the operator put on the box. A list written in advance does
not survive the first new model.

**It also depends on what is on disk.** A recipe is declared only once its weights are proven
loadable; a background thread fetches the rest and calls `Worker.redeclare(...)` as each one lands.
The node picks that up on its next liveness ping — within `Tools:MaintenanceInterval`, 30 s by
default — re-narrows it against the manifest, and re-reports to its coordinator. **No restart, and
no request ever waits on a download**, which is the whole of what v3.14.1 fixed.

`redeclare` is the only protocol addition in that release, and it is additive: a worker that never
calls it behaves exactly as it did, and an older node ignores the frame.

What it reports also depends on the card. `sd15` is marked `cpuViable` and `sdxl` is not, so a
CPU-only node offers only the first — the hub then never routes `sdxl` there, rather than routing it and
making the caller discover a four-minute request. With `INFERHUB_IMAGE_REQUIRE_GPU=1` (the default,
from `Tools:Image:RequireGpu`) the worker does not start at all without CUDA, and says which key to
unset. `INFERHUB_IMAGE_ALLOW_SLOW_CPU=1` offers the rest anyway.

The device is on the first log line, for the same reason Whisper's is.

### One pipeline at a time

Loading SDXL is tens of seconds, so a worker that reloaded per request would spend more time loading
than generating — and a worker that kept several resident would need the VRAM budget that has not
been built yet. So it holds one, and frees the old pipeline **before** allocating the new one: doing
it the other way round makes the peak both models at once, and the box OOMs on the swap rather than
on the load.

`maxWorkers` is 1, as everywhere else. A second pipeline on the same card is a second copy of the
weights and an out-of-memory error at the worst possible moment.
