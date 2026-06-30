Self-healing replicas. v1.8.0 closes the durability loop: when a node holding a
collection's replica drops, the coordinator quietly rebuilds it on a surviving node
from its raw store, back to the target replication factor — no operator action.
Even if every node holder of a collection disappears, the hub-local index keeps
answering reads and the next eligible node is seeded from raw. The always-on
coordinator's raw store is the anchor the whole thing leans on.

## What's new

- **HealingService** (new hosted service). Watches `INodeRegistry.Changed` and
  `LocalVectorStore.CollectionCreated`. Each event schedules a debounced heal pass
  (default 750 ms) that recomputes placement for every collection. A heal pass
  drops stale placements, then re-pushes from the raw store to fill the target
  replica count. A single `SemaphoreSlim` guards re-entrant heals — a flapping
  fleet collapses into one settled pass rather than a storm.
- **Last-node-down recovery.** When the live replica count for a collection hits
  zero the hub-local index continues to serve reads (the floor since v1.7), and
  the next eligible node — survivor or freshly joined — is seeded from the raw
  store. The "raw store on the always-on host is the anchor" invariant is
  preserved without operator action.
- **New-node seeding.** A node joining while the fleet is under target now picks
  up a replica as part of the same heal loop.
- **`POST /api/admin/vector/collections/{collection}/rebuild`** — admin-scoped,
  audit-logged. Forces a heal-to-target re-push from the raw store; useful to
  proactively re-seed after a fleet event or to confirm replica integrity. Returns
  `{ collection, rebuilt: true }` on success, `404` on unknown collection.
- **Heal-loop separation.** `ReplicationCoordinator` no longer subscribes to
  fleet changes directly — it now owns the placement primitives (`RecomputeAsync`,
  fan-out, snapshot/pin) and `HealingService` owns the loop. The two responsibilities
  were already in tension in v1.7's coordinator; this split makes the heal cadence
  explicit and testable.
- **Metrics counters.** `vectorReplicasHealed` (each replica added by a heal),
  `vectorRebuildsFromRaw` (each last-node-down or admin rebuild), and
  `vectorUnderReplicated` (gauge of collections currently below target) are now in
  the `MetricsSnapshot`. Surfaced in the console in phase 17.
- **Config.** New `VectorStore:Healing:DebounceMilliseconds` (default `750`,
  min `50`) and `VectorStore:Healing:IdleSweepSeconds` (default `15`, min `1`)
  control the heal cadence and the periodic gauge refresh.

## Compatibility

- No breaking changes for callers. With `VectorStore:Enabled=false` (the default)
  nothing in this release runs; the no-persistence contract is unchanged.
- Existing replication behaviour from v1.7 is preserved: writes still hit the raw
  store first, then fan out to current holders; reads still prefer a node replica
  with the hub-local index as the floor. The visible change is that placement is
  now reconciled by a debounced loop instead of synchronously on every fleet event.
- `MetricsSnapshot` gained three new fields. Consumers using its constructor
  positionally need to add the new arguments; property access by name is unchanged.

## What's next

Phase 17 (`v1.9.0`) surfaces collections, replica health, and live healing events
in the build-free console — same dark-theme HTML/CSS/JS, same SSE stream — plus a
full vector-store section in the README. Then phase 18 (`v2.0.0`) wires retrieval
straight into `/api/chat` and `/api/generate` for the GA RAG mesh.
