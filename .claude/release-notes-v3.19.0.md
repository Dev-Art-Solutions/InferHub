# InferHub v3.19.0 — a fleet that makes pictures, and one page to run it from

Phase 51 closes the image track the way v3.13 closed the last one: six releases of capability become
something a person who did not write them can operate.

**If you read one release note about v3.14–v3.19, read this one.**

## What the track added

| | |
|---|---|
| **v3.14** | Text to image on OpenAI's Images API, Stable Diffusion on your own card, a fifth `:diffusion` container — and the capability seam carried a whole new modality with **no protocol change at all** |
| **v3.15** | Async jobs: a place in line, per-step progress over SSE, cooperative cancel that keeps the worker warm, results that live in memory for five minutes and nowhere else |
| **v3.16** | A catalogue: seven models, nf4 quantization that fits a 20B transformer and its 8.3B text encoder on one consumer card, a **VRAM budget you declare rather than one we guess**, hub-driven weight pulls, per-model licence consent |
| **v3.17** | `qwen-360`: 360° equirectangular panoramas from a rank-128 LoRA over that 20B base, projection **declared** rather than guessed from an aspect ratio, a seam that is measured and never repaired, a hand-written WebGL viewer |
| **v3.18** | Editing: inpainting, img2img and variations, with the mask convention converted where a pixel may actually be read |
| **v3.19** | This one: the console, the metrics and the docs for all of it |

Six releases. **Zero new dependencies.** PyTorch is a child process, not a package — and that
sentence now lives in design rule 5 rather than only in the phase that decided it.

## The console shows the job, not just the node

Phase 45's console answers "I turned it on and nothing happened". This track produces a different
confusion: **"it is running and I cannot tell how far along."**

So `/console.html` → **Images** is job-centric: every job with its queue position, a step bar at
*n* of *m*, elapsed time, which node has it, the megapixel-steps it cost, and a **cancel button**.
Cancel is cooperative and the UI says so rather than pretending — a job cancelled at step 27 of 28
may still finish, and if it does you get the picture.

Underneath it: every recipe on the fleet, and **why** each one is or is not offered.

## The gap this phase found was v3.16's

A recipe whose licence nobody accepted, or one too big for the declared VRAM budget, is **not
declared** — the fleet never routes at it, so nobody spends a request finding out. That is the right
routing behaviour and it is the worst possible diagnostic: at the hub it is indistinguishable from a
recipe that does not exist, from one whose weights are still downloading, and from a typo in a
config file. Four causes, four different fixes, one symptom: nothing.

So the node now reports every recipe it holds with a reason:

| Recipe | Offered for | Why not |
|---|---|---|
| `sdxl` | generate, edit | — |
| `sd15` | generate, edit | — |
| `sdxl-turbo` | — | licence `sai-nc-community` is not permissive and is not in `Tools:Image:AcceptedLicenses` |
| `qwen-image` | — | wants 19000 MiB; `BudgetMiB: 24576` minus `ReserveMiB: 8192` leaves 16384 |

**The order of those checks is the order of the fixes.** A recipe that is both unlicensed and
oversized reports `unlicensed`, because telling somebody to buy a bigger card for a model they may
not be allowed to run is the wrong advice in the wrong order.

`unlicensed`, `over-budget` and `narrowed` also appear on the **Needs attention** strip above the
fold. `not-ready` deliberately does not: weights that are still downloading are a fleet working
correctly, and a strip that fired on every cold start is a strip people learn to close.

## The gallery is your browser's, and that is a refusal

Thumbnails in the Images panel are object URLs in that tab. They vanish on reload. **There is no
server-side gallery, no history endpoint and no thumbnail cache**, and the panel says so in one line.

This is where v3.14's "no URL in the response" and v3.15's "results live in memory for five minutes"
arrive at their conclusion. A console gallery is exactly the pressure that turns a bounded in-memory
job store into an image archive — it is the feature request that sounds harmless and ends with a
retention policy, a deletion endpoint and a question about whose pictures those are. The place to
refuse it is in writing, before somebody adds a cache "just for the console".

## The series to alert on

```
inferhub_image_recipe{node,recipe,reason}       # ← the two worth an alert:
                                                #   reason="unlicensed" and reason="over-budget"
inferhub_image_jobs_total{recipe,outcome}
inferhub_image_job_seconds{recipe}              # a real histogram, from SUBMISSION
inferhub_node_vram_budget_mib{node}
inferhub_node_vram_resident_mib{node}
inferhub_node_vram_measured_mib{node}           # the worker's own reading, beside the declared one
inferhub_image_queue_depth                      # fleet gauges: always present, at zero
```

Two rules carried forward, and this release has both halves side by side:

- **Fleet gauges are present at zero.** A hub with an image queue and nothing in it is saying
  something, and a dashboard cannot tell "idle" from "not scraped" otherwise.
- **Everything else is absent until there is something to say.** A recipe nobody has rendered with
  emits nothing. **A node with no declared VRAM budget emits no VRAM series at all** — because
  `budget_mib{node}=0` reads as "this box has no VRAM", which is a different and false statement
  from "nobody declared a budget on this box".

The duration histogram measures from **submission**, not from dispatch: a job that spent four
minutes queued behind somebody else's batch was four minutes slow, whatever the GPU was doing. And
it carries a `+Inf` bucket, without which the series is not a histogram and `histogram_quantile`
returns nothing rather than an obviously wrong number.

## One new route

`GET /api/images/jobs` — this client's jobs, oldest first, with the queue's own depth beside them.
v3.17 deferred it here by name: a panel that shows "what is this fleet rendering right now" cannot
be built from five routes that all need a job id you already have.

It is **client-scoped**, like every other route in that group. Holding a job id is how you fetch the
picture, so an admin console listing every tenant's ids would be an authorization mistake wearing a
UI. The Images panel therefore holds its own **client** key — the admin key the rest of the console
uses will not open it.

And it lists **work, not results**: a delivered job is still in the list, still says so, and has
nothing left to fetch.

## Upgrading

Nothing to do. Every change is additive and every new field defaults to today's behaviour: a node
that reports no image recipes produces no panel rows and no series, and a v3.18 node against a v3.19
hub simply omits the new block.

---

**Zero new dependencies.** No bundler, no framework, no CDN script — the progress bar is two divs and
a width percentage. `InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.
`dotnet test`: 1195 passed, 0 failed, 48 skipped.
