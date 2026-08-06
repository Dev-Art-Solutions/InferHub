# InferHub 3.15.0 — what a two-minute job should look like

Phase 47 of the image track. v3.14 put Stable Diffusion on the fleet behind OpenAI's Images API. The
thing it did not have was any way to **watch** one: you sent a request, held a connection open, and
the only two outcomes you could observe were "it worked" and "something timed out" — usually from a
proxy in the path, with a status nobody could act on.

An SDXL render at 50 steps is not a request. It is a job.

```bash
# submit
ID=$(curl -sS http://localhost:5080/api/images/jobs -H "Authorization: Bearer $KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"sdxl","prompt":"a lighthouse in a storm","size":"1024x1024"}' | jq -r .id)

# watch  (SSE: queued → running(step 7/28) → succeeded)
curl -N http://localhost:5080/api/images/jobs/$ID/events -H "Authorization: Bearer $KEY"

# collect (read-once, in memory, expiring)
curl -sS http://localhost:5080/api/images/jobs/$ID/content/0 -H "Authorization: Bearer $KEY" -o out.png

# or change your mind
curl -X DELETE http://localhost:5080/api/images/jobs/$ID -H "Authorization: Bearer $KEY"
```

**Zero new dependencies. No new model. `InferHub.Shared.csproj` is still empty.**

## `/v1/images/generations` is unchanged

If you never read this page, nothing happened. Same body, same headers, same envelope, same statuses.

Internally it became "submit a job and wait for it", so a synchronous call and an asynchronous one
queue in the **same line** and are metered by the **same code** — two paths to a GPU with two ideas
of fairness is how a fleet grows a fast lane nobody documented. Past `Images:SyncMaxWaitSeconds`
(120) it now answers `503` naming the job id and the async route, and **the job keeps running**:
throwing away a minute of GPU because an HTTP client got bored is your decision, not the hub's.

## There is no `background: true` flag

OpenAI has no asynchronous Images API to adopt, and where there is no dialect to adopt this project
does not invent an OpenAI-shaped one — work with no existing shape travels as its own contract under
`/api`. A flag on `/v1/images/generations` returning a non-OpenAI body would make one route answer
two incompatible shapes, which every typed SDK gets wrong, and would turn "is this endpoint
OpenAI-compatible?" into a question with a footnote. This repository has refused that trade five
times before.

## Cancel does not kill the worker

A `DELETE` sends a `cancel` frame to the worker, which honours it from its per-step callback and
answers with an error coded `cancelled`. Then it is **still alive, still holding its weights**, and
the next job starts without reloading them.

Killing it would be simpler and is wrong: a diffusion worker's weights took tens of seconds to load —
twelve to twenty gigabytes taking a minute or more once the catalogue grows — so killing it to
abandon one job punishes the *next* caller, and the punishment gets worse with every model added. A
worker that has not answered within `Tools:CancelGraceSeconds` (20) is by definition not cooperating,
and *that* one is terminated and restarted.

**Cancellation is best-effort and the API says so.** A job cancelled at step 27 of 28 may still
succeed, and if it does you get your image. Discarding a finished result to honour a state name would
be worse than telling you what actually happened. The test suite asserts that outcome as **legal**,
not flaky.

## Results live in memory for five minutes and nowhere else

- `Images:Jobs:RetentionSeconds` (300) — a finished job's record and bytes are dropped this long
  after completion, read or not.
- `Images:Jobs:MaxRetainedBytes` (512 MB) — a global ceiling, LRU-evicting **completed** results and
  never an in-flight one, enforced **on insert** rather than on a timer. A timer means the ceiling is
  a suggestion for one sweep interval, and one sweep interval of 4096² batches is how a hub gets
  OOM-killed.
- **Read-once by default.** A delivered image is dropped immediately; a second `GET` is a `410` that
  says *why*, not a `404` that reads like a bug. `Images:Jobs:KeepAfterRead` exists for a console's
  benefit and is documented as the setting that makes the hub briefly an image cache.
- **Nothing touches disk. Ever.** No temp file, no spill under memory pressure, no cache directory.
  Under pressure the answer is eviction and a `503` on submit, not a file.

A restart forgets in-flight and completed jobs, exactly like every other counter on the hub. That is
not a limitation waiting to be fixed — it is what keeps "no persisted state" true, and durable jobs
would have to be argued as a fourth exception to that rule rather than added quietly.

## The queue is FIFO and it is not clever

A GPU running diffusion is a resource there is exactly one of, so the hub gives each capable node one
image job at a time and takes the queue in order. A queued job answers `202` with a **place in
line**, not a wait-then-503 — you already accepted asynchrony, so making you retry would be strictly
worse than telling you where you are.

Shortest-job-first would let a stream of four-step requests starve a fifty-step one indefinitely and
the starvation would be invisible; fair-share needs a notion of tenant weight this project's client
model does not have. `Images:Jobs:MaxQueueDepth` (32) bounds it, and a full queue is `503` +
`Retry-After` — the same status and header as every other limit here, so your retry logic behaves
identically whichever one it hit.

## A node that disappears mid-job fails the job, and never retries it

An image job that died at step 22 produced no output, so it is *technically* retryable — and retrying
it would silently double the GPU-minutes and the ledger units for one request. It fails with
`node_lost` and you decide. A job still `queued` when its node goes away has spent nothing and is
simply routed again.

## Every route is yours only

A job id belonging to another client is a `404`, byte-identical to one that does not exist — never a
`403`. On a surface whose ids are only knowable by having been issued one, the difference between
"not yours" and "not there" *is* the isolation boundary. The suite compares the two **bodies**, not
just the two statuses.

## Solo mode gets the same five routes

A standalone node serves `/api/images/jobs` itself, with the same bodies and the same statuses — one
job at a time, because it is a box with a card in it. The deployment least likely to have a proxy
that tolerates a two-minute request is the one somebody is running on a laptop.

## For worker authors

The protocol gained two frames, both additive — a worker written against 3.14 never sends `progress`,
never receives `cancel`, and behaves exactly as it did:

- **`progress`** (worker → node): `{"type":"progress","id":"…","step":7,"totalSteps":28}`
- **`cancel`** (node → worker): `{"type":"cancel","id":"…"}` — answer with an `error` frame coded
  `cancelled`, and **stay alive**.

The Python reference library does both in two lines inside your step callback:

```python
for step in range(1, steps + 1):
    ...
    request.progress(step, total_steps=steps)
    request.raise_if_cancelled()
```

One thing worth copying rather than rediscovering: its read loop now uses `readline()` instead of
`for line in sys.stdin`. The iterator protocol keeps a read-ahead buffer, and the frame two readers
would lose between them is the cancel.

## Config

| Key | Default | What it does |
|---|---|---|
| `Images:SyncMaxWaitSeconds` | `120` | How long `/v1/images/generations` waits for its own job before a `503` naming the async route. The job keeps running. |
| `Images:Jobs:RetentionSeconds` | `300` | How long a finished job's record and bytes survive. |
| `Images:Jobs:MaxRetainedBytes` | `536870912` | Global ceiling on retained results; LRU-evicts completed ones, enforced on insert. |
| `Images:Jobs:KeepAfterRead` | `false` | Off: a delivered image is dropped. On: the hub is briefly an image cache. |
| `Images:Jobs:MaxQueueDepth` | `32` | How many jobs may wait. Full is a `503` + `Retry-After`. |
| `Tools:CancelGraceSeconds` | `20` | *(node)* How long a worker gets to honour a cancel before it is terminated and restarted. |

## Upgrading

Nothing to do. A deployment that changes no config behaves exactly as it did on 3.14: the job routes
are mapped and inert, the store holds nothing, the sweeper ticks over zero jobs, and the synchronous
image route answers what it always answered.
