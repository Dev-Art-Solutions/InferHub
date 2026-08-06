# Blog post — v3.15.0

- **slug**: `inferhub-3-15-a-job-that-takes-two-minutes`
- **title (EN)**: `InferHub 3.15: what a two-minute job should look like`
- **visibility**: EN visible, BG hidden — **but see the note at the bottom**
- **Cloudflare WAF**: no shell commands anywhere in the body. Show the JSON, never a `curl`.
- Content is stored **entity-escaped**; that is correct and is how every prior post is stored.

## Excerpt (EN)

An SDXL render at 50 steps is not a request, it is a job. InferHub 3.15 gives image generation a
clock: a job id, a place in line, per-step progress over SSE, and a cancel that does not kill the
worker. Results live in memory for five minutes and nowhere else — and `/v1/images/generations` is
unchanged for anyone who never reads this.

## Body (HTML, to be entity-escaped by the connector)

<p>Version 3.14 put Stable Diffusion on the fleet behind OpenAI's Images API. It worked. What it did
not have was any way to <em>watch</em> one.</p>

<p>You sent a request and held a connection open, and the only two things you could learn were "it
worked" and "something timed out" — usually from a proxy in the path, with a status nobody could act
on. An SDXL render at 50 steps is not a request. It is a job, and 3.15 treats it as one.</p>

<p>Submit, and you get an id and a place in line:</p>

<pre><code>POST /api/images/jobs
{ "model": "sdxl", "prompt": "a lighthouse in a storm", "size": "1024x1024" }

202 Accepted
{ "id": "9f2c…", "state": "queued", "model": "sdxl", "n": 1, "queuePosition": 2 }</code></pre>

<p>Watch it on <code>GET /api/images/jobs/{id}/events</code>, which is server-sent events:</p>

<pre><code>event: running
data: { "id": "9f2c…", "state": "running", "node": "gpu-01", "step": 7, "totalSteps": 28 }

event: succeeded
data: { "id": "9f2c…", "state": "succeeded", "megapixelSteps": 29.4,
        "images": [ { "index": 0, "url": "/api/images/jobs/9f2c…/content/0",
                      "size": "1024x1024", "seed": 4242, "bytes": 1483920 } ] }</code></pre>

<p>Collect the bytes from that URL, once. Or change your mind with a <code>DELETE</code>.</p>

<h2>There is no <code>background: true</code> flag</h2>

<p>The obvious design is a flag on the endpoint you already have. We did not do that, and the reason
is the same one this project has given five times before.</p>

<p>OpenAI has no asynchronous Images API to adopt. Our rule is "adopt the dialect clients already
speak" — and where there is no dialect to adopt, that rule does not say "invent an OpenAI-shaped
one". A <code>background: true</code> field on <code>/v1/images/generations</code> returning a
non-OpenAI body makes one route answer two incompatible shapes depending on a flag, which every
typed SDK in every language gets wrong, and it turns "is this endpoint OpenAI-compatible?" into a
question with a footnote. So the async surface is our own honest contract, under
<code>/api</code>, and the OpenAI-compatible route stays exactly OpenAI-compatible.</p>

<p><strong><code>/v1/images/generations</code> is unchanged.</strong> Same body, same headers, same
envelope, same statuses. Internally it became "submit a job and wait for it" — a refactor with no
observable difference and a test that says so field-for-field. That was not tidiness: it means a
synchronous call and an asynchronous one queue in the <em>same line</em> and are metered by the
<em>same code</em>. Two paths to a GPU with two ideas of fairness is how a fleet grows a fast lane
nobody documented.</p>

<p>The one thing it gained is an honest ceiling. Past <code>Images:SyncMaxWaitSeconds</code> (120) it
answers <code>503</code> — naming the job id and the async route, because <strong>the job keeps
running</strong>. Throwing away a minute of GPU because an HTTP client got bored is the caller's
decision, not the hub's.</p>

<h2>Cancel does not kill the worker</h2>

<p>This is the decision I would defend hardest, and it is the one that looks like extra work for
nothing until you think about the second caller.</p>

<p>A <code>DELETE</code> sends a <code>cancel</code> frame down to the diffusion worker. The worker
honours it from its per-step callback, answers with an error coded <code>cancelled</code>, and is
then <em>still alive and still holding its weights</em>. The next job starts without reloading
them.</p>

<p>Killing the process would have been three lines. It is also wrong. A diffusion worker's weights
took tens of seconds to load — and twelve to twenty gigabytes taking a minute or more once the model
catalogue grows. Killing it to abandon <em>one</em> job punishes the <em>next</em> caller, who did
nothing, and the punishment gets strictly worse with every model added. A worker that has not
answered within <code>Tools:CancelGraceSeconds</code> (20) is by definition not cooperating, and
<em>that</em> one is terminated and restarted. Cooperation first, the axe as a fallback.</p>

<p>And cancellation is <strong>best-effort, which the API says out loud</strong>. A job cancelled at
step 27 of 28 may finish anyway — the state machine allows <code>cancelling → succeeded</code>, and
if it happens you get your image. Discarding a finished result to honour a state name would be
worse than telling you what actually happened. The test suite asserts that outcome as
<em>legal</em>, not as a flake to be retried away.</p>

<h2>Results live in memory for five minutes and nowhere else</h2>

<p>The moment a hub holds a finished image, somebody has to answer "for how long, and whose is it".
Here is the whole answer:</p>

<ul>
<li><strong>Five minutes</strong> (<code>Images:Jobs:RetentionSeconds</code>), read or not.</li>
<li><strong>Read once.</strong> A delivered image is dropped immediately. The second fetch is a
<code>410</code> that says <em>why</em> — not a <code>404</code> that reads like a bug. "You were too
late" and "that never existed" are different problems with different fixes.</li>
<li><strong>512 MB, LRU</strong> (<code>Images:Jobs:MaxRetainedBytes</code>), evicting completed
results and never an in-flight one — and enforced <em>on insert</em>, not on a timer. A timer means
the ceiling is a suggestion for one sweep interval, and one sweep interval of 4096² batches is how a
hub gets OOM-killed.</li>
<li><strong>Nothing touches disk. Ever.</strong> No temp file, no spill under memory pressure, no
cache directory. Under pressure the answer is eviction and a <code>503</code> on submit, not a
file.</li>
</ul>

<p>A restart forgets in-flight and completed jobs, exactly like every other counter on the hub. That
is not a limitation waiting to be fixed. It is what keeps "no persisted state" true — and if a
future release wants durable jobs, that has to be argued as an exception to the rule rather than
added quietly in an endpoint. The moment a result survives a restart, "where are my pictures kept"
stops having the answer "nowhere, for five minutes" and becomes a data-retention question somebody
has to own.</p>

<h2>The queue is FIFO and it is not clever</h2>

<p>A GPU running diffusion is a resource there is exactly one of. So the hub gives each capable node
one image job at a time and takes the queue in order.</p>

<p>Shortest-job-first is the tempting alternative and it is a trap: a steady stream of four-step
requests would starve a fifty-step one indefinitely, and the starvation would be completely
invisible — no error, no timeout, just a job that never runs. Fair-share-by-client needs a notion of
tenant weight our client model does not have, and this was not the release to invent one.</p>

<p>A queued job answers <code>202</code> with a <em>place in line</em> rather than the usual
wait-then-<code>503</code>: you already accepted asynchrony, so making you retry would be strictly
worse than telling you where you are. A full queue is <code>503</code> with
<code>Retry-After</code> — the same status and header as every other limit in this codebase, so your
retry logic behaves identically no matter which one it hit.</p>

<h2>A node that vanishes mid-job is never silently retried</h2>

<p>An image job that died at step 22 produced no output, so it is <em>technically</em> retryable.
We do not retry it.</p>

<p>Retrying would silently double the GPU-minutes and the billed units for one request — the caller
asked for one image and paid for two, and nothing anywhere would say so. It fails with
<code>node_lost</code> and you decide. A job still <em>queued</em> when its node goes away has spent
nothing, so that one is simply routed again. The test takes a real node away mid-job and asserts
both the reason and an <em>empty</em> ledger, because a silent retry would have doubled those numbers
rather than left them empty.</p>

<h2>If you write workers</h2>

<p>The protocol gained two frames and both are additive — a worker written against 3.14 never sends
<code>progress</code>, never receives <code>cancel</code>, and behaves exactly as it did.</p>

<pre><code>worker → node   { "type": "progress", "id": "…", "step": 7, "totalSteps": 28 }
node   → worker { "type": "cancel",   "id": "…" }</code></pre>

<p>In the Python reference library that is two lines inside your step callback:</p>

<pre><code>for step in range(1, steps + 1):
    ...
    request.progress(step, total_steps=steps)
    request.raise_if_cancelled()</code></pre>

<p>One detail worth copying rather than rediscovering at 2am: the reference loop now reads with
<code>readline()</code> instead of <code>for line in sys.stdin</code>. The iterator protocol keeps a
read-ahead buffer, and once a worker reads control frames while a request is running there are two
readers on one stream — the frame they lose between them is the cancel.</p>

<h2>Solo mode gets the same five routes</h2>

<p>A standalone node serves <code>/api/images/jobs</code> itself, same bodies and same statuses, one
job at a time — because it is a box with a card in it. That is not a consolation prize: the
deployment least likely to have a proxy that tolerates a two-minute request is the one somebody is
running on a laptop.</p>

<h2>Nothing to do to upgrade</h2>

<p>A deployment that changes no config behaves exactly as it did on 3.14. The job routes are mapped
and inert, the store holds nothing, and the synchronous image route answers what it always answered.
Zero new dependencies, no new model, and the shared library still has an empty project file.</p>

<p>Next in this track: a catalogue of six models with real memory budgets and a quantization path,
360° panoramas, and editing — img2img, inpainting and variations.</p>

## Note on publishing (2026-08-06)

**GitHub Actions did not fire for the `v3.15.0` tag**, so `ghcr.io/dev-art-solutions/*:3.15.0` does
not exist yet — the last workflow run on the repo is for the *previous* commit (`4202334`), nothing
is queued, waiting or in progress, and `3.14.1` still pulls fine as a control. Almost certainly an
org-level Actions spending limit; it needs an org admin to check, and neither workflow has a
`workflow_dispatch` trigger so it cannot be forced from the CLI.

Publish this **as a draft (both languages hidden)** and flip it visible from the site's admin once
the images are actually pullable. The connector is insert-only and the slug locks, so the slug is
worth claiming now; the visibility is not.
