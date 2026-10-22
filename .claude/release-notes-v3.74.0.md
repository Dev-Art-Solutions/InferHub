# InferHub v3.74.0 — a free slot worth waiting for

Phase 110. v3.72 sends a chat to a node with a free slot for its model, and v3.73 lets an engine say how many it has.
When **no** holder had one, the chat went anyway — to the least busy node, where it waited inside that node's engine.
Two nodes with one slot each and a third chat: the third was committed to node A's queue the moment it arrived, and
if node B finished first, B sat idle while it waited for A.

- **`Queue:SlotWaitSeconds` lets it wait at the hub instead**, and leave for whichever node frees a slot first. Off by
  default (`0`), which is v3.73 exactly.
- **It never refuses.** No slot within the bound, or more than `Queue:MaxDepth` already waiting: the request goes on
  to queue in an engine as it always did. A slot wait is never a `503` — before this release that request was
  answered, and it still is.
- **One freed slot lets one request go.** Every waiter would see a freed slot at the same instant and all of them
  would land on it. Waiters for a model leave one at a time, in arrival order, and the next is let go only once the
  previous one is counted against its node.
- **"Full" is one definition**, now shared by the router and the wait: a node declared a slot count for the model (the
  engine's own since v3.73, else `Node:ModelSlots` / `OLLAMA_NUM_PARALLEL`) and runs that many of the hub's jobs for
  it. A node that declared nothing is always free, so nobody waits on its account.
- A node at its `MaxConcurrency` is still the bounded queue's business (v2.7), and a full slot never bursts to a cloud
  provider: it means "this engine will queue it", not "the fleet cannot answer".

## What changes with no config changed

Nothing. `SlotWaitSeconds` defaults to `0`; `/api/status`'s `queue` block has no `slotWait` key and `/metrics` no
`inferhub_queue_slot_*` series until it is set. The router's free-slot tier reads the same predicate it computed
inline before.

With the key set, `GET /api/status` adds `queue.slotWait: {depth, waited, admitted, sentAnyway}` and `/metrics` adds
`inferhub_queue_slot_depth` and three counters.

## Found by running it

A real coordinator and **two** nodes from source, `Node:ModelSlots=1` each, over the one Ollama on this box
(`OLLAMA_NUM_PARALLEL=1`, `qwen2.5:0.5b`), three chats fired 0.3 s apart:

- **`SlotWaitSeconds=20`:** the first two went to `live-a` and `live-b`; `/api/status` read `slotWait.depth: 1` at
  one second — the third was at the hub — then `admitted: 1`, and the third was dispatched to the node whose chat
  had finished first. All three `200`.
- **`SlotWaitSeconds=0`:** the third was dispatched at once, to a node already running one; `/api/status` had no
  `slotWait` key at all.

Both nodes drove the same Ollama, so the wall-clock times were the same either way (1.1 / 1.6 / 2.1 s) — one engine
runs one request at a time however the hub orders them. What the run shows is **where and when the hub sent the
third chat**, which is the claim; the time saved needs two engines, and this box has one.

## The published binaries

The published `inferhub-coordinator:3.74.0` and `inferhub-node:3.74.0` images (revision label `9f5e8cd`, the tag
commit) were pulled from GHCR and their `/app` binaries run natively, the same two nodes over the same Ollama, which
this time was also holding another 35B model of another project's — so each chat took ~10 s instead of ~0.5 s, and the
wait was easier to watch. Both nodes registered as `3.74.0` with `modelSlots: 1`.

- **`SlotWaitSeconds=20`:** `slotWait.depth: 1` on every one of six status reads, one a second; the third chat left only
  when the first finished (11.5 s) and went to that chat's node; the run ended `waited 1, admitted 1, sentAnyway 0`.
  All three `200`.
- **`SlotWaitSeconds=0`:** no `slotWait` key; the third chat was dispatched at once to `live-b`, which was already
  running the second.

No exception in any log but a node seeing its hub stopped between the two runs. This exercised the binaries, not the
containers' OS layer.

## A build artefact the full suite tripped on

The first full run had two `ChatGptBackendTests` red in `Tests.Node`. Not this phase: the coordinator and the node
both ship an `appsettings.json`, both are copied into the node test project's `bin/`, and editing the coordinator's
made the incremental build copy it last — so the node tests read the coordinator's `ChatGpt:DataDirectory`. They fail
identically with this phase's changes stashed, and pass once the node's file is the newer one again. A clean build
(CI) is unaffected; recorded because it will happen again to the next phase that edits the coordinator's
`appsettings.json`.

## Not established

- **The time saved on a real two-engine fleet.** The live run had one Ollama behind both nodes (above).
- **A slot wait that runs out, live.** `AWaitThatRunsOutSendsTheRequestAnywayNeverA503` covers it against the real
  router and `InferenceCore`; the live run's waits all ended in a free slot.
- **Streams.** The turnstile is released once the dispatch has counted the job, and `Dispatcher` does that before its
  first await on the streaming path too (read, and the code comment says so); the live run and the tests used
  blocking chats.

## Tests

`SlotWaitTests` (Coordinator): three chats over two one-slot nodes through `InferenceCore` with the real `Router` —
the third waits and goes to the node that frees first (**red with the wait removed**); three waiters and one freed
slot let one go (**red with the turnstile removed**); a wait that runs out dispatches and answers; off by default the
status block carries no `slotWait`; past `MaxDepth` straight on; a caller who leaves stops waiting; 109's per-model
count is what "full" means.

The routing decisions moved: `src/InferHub.Coordinator/CLAUDE.md` was at 1099 of 1100 lines, so phases 86 (the hub's
half) and 89 moved **unedited** into a new `src/InferHub.Coordinator/Services/CLAUDE.md`, where 110 joins them.

`dotnet test InferHub.sln`: **2 317 passed / 63 skipped** (after the build artefact above was cleared). Zero new `PackageReference`; `InferHub.Shared.csproj` is still empty.
