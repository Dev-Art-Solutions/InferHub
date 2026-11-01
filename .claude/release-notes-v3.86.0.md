# InferHub v3.86.0 — priority classes

Phase 126. When nodes fail or every node is busy, the hub has so far treated all work the same: the next request in took
the next free place, whoever sent it. A cluster that runs work that must not stop next to work that can wait needs the
hub to tell them apart. Give each client a class. When capacity runs short, background work waits and essential work keeps
running.

```bash
# on the coordinator
Priority__Enabled=true                       # off by default
Auth__Clients__0__Priority=essential
Auth__Clients__2__Priority=background        # unset = normal
Priority__ReserveSlots=1                     # free places background work may not take (default 1)
Priority__BackgroundMaxWaitSeconds=600       # how long it waits before a 503 (default 600)
```

## What it does

- **Three classes per client:** `essential`, `normal` (the default) and `background`. Anonymous keys are normal. A misspelt
  class fails startup, because it would otherwise run quietly as normal.
- **Background work leaves the last free places alone.** The hub counts the model's free places on the nodes it can route
  to now: per node, what is left of its `MaxConcurrency` and of the model's slots, the lesser of the two. Background work
  goes only while more than `ReserveSlots` are free; otherwise it waits (paused, not refused) up to
  `BackgroundMaxWaitSeconds`, then gets a 503 naming the key.
- **A node lost is capacity lost.** A node that disconnects, is paused by its owner, is over its own limits or is cordoned
  is already out of the count, so when nodes fail, background work is what waits first.
- **A wait does not overtake a higher class.** Requests waiting for a full fleet (`Queue:MaxWaitSeconds`) go in class
  order for the same model.
- **Essential work is not held for a slot.** With `Queue:SlotWaitSeconds` set, essential work goes to the engine at once.
- **A request may lower its own class** with `X-InferHub-Priority: background`. Asking for a higher class is ignored.
- **Visible.** `/api/status` gains `priority` (per class: waiting, held, released, timed out, requests); `/metrics` gains
  `inferhub_priority_reserve_slots` and five `inferhub_priority_*{class=…}` series; the audit trail (3.85) gains
  `priority`. All only while it is on.

## Upgrading

Nothing changes unless you set `Priority:Enabled`. With it off every request is normal, nothing waits that did not wait
before, and the status payload, the scrape and the audit lines are those of 3.85. No node change, no wire change, no new
package.

## Not established

- **Stopping running work.** A running background job is never stopped to make room; it finishes. Only new work waits.
- **Embeddings, tools, audio, images and video.** They do not pass through the path that classes work.
- **A node that declares neither a concurrency cap nor slots** gives the hub nothing to count, so nothing is held for it.
  Every Ollama node declares slots (one by default), so on a normal fleet this does not arise.
- **The capacity-queue ordering live.** It is tested with the real queue (and red with its condition removed), but the live
  run's nodes declared slots, not `MaxConcurrency`, so that queue was not entered live.
- **Many classes of clients under real load.** The live run had one client per class.

## Verification

- Suite: 2 727 passed, 63 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 987, Node 728, Mesh 543). New: `PriorityTests` (16, Coordinator — the real `Router` and `RequestQueue` through `InferenceCore`:
  off by default; the reserve held and released; a node removed makes background wait while essential and normal run; the
  503; an unbounded node; the header table; essential first in the capacity wait; essential past the slot wait; the
  validators; the scrape).
- Live, from source: a hub with priority and the audit trail on, keys required, clients `ops` (essential), `team` (normal),
  `batch` (background); two nodes (`Node:ModelSlots=1`) on the real Ollama with `qwen2.5:0.5b`.
  - A long `team` story took one of the two places. `batch` was held (`waiting 1, held 1`); `ops` was answered in 2.5 s
    while it waited; `batch` went when the story finished.
  - gpu-b stopped through its control API. `batch`, and a `team` request that sent `X-InferHub-Priority: background`, both
    waited; `ops` (0.29 s) and `team` (0.02 s) were answered. When gpu-b registered again both went within about three
    seconds (13.4 s and 10.6 s in all).
  - The audit lines carried `essential`, `normal` and `background`, the lowered one included; `/metrics` said background
    held 3, released 3, timed out 0.

## The published image

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.86.0` carries revision `6ba0d0a`. Docker Desktop was not running, so the
image's `/app` was pulled through GHCR's registry API and run natively (`/health` said `3.86.0`), with the same three
clients and two nodes from source at the tag on the real Ollama.

- gpu-b stopped through its control API: a `batch` request waited (`background: waiting 1, held 1`); `ops` was answered in
  0.33 s and `team` in 0.02 s on the remaining node.
- gpu-b registered again and the `batch` request went (10.6 s in all). The audit lines carried `essential`, `normal`,
  `background`, and the node leaving and returning.
- Not run: the container itself.
