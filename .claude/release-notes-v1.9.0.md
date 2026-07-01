Console & observability for the vector mesh. v1.9.0 makes the phase 13–16 machinery
visible and operable: collections, replica placement, and healing events now show up
in the same build-free console — no framework, no toolchain — and the admin SSE
stream carries vector lifecycle events alongside the existing node snapshots.

## What's new

- **Status JSON gains a `vector` block.** `GET /api/status` now includes per-collection
  record count, dimension, distance, target vs live replicas, holder node ids, and an
  `underReplicated` flag. Omitted when the store is disabled — the phase-13 contract
  that `Enabled=false` leaves existing consumers unchanged is preserved.
- **Live vector events on `/api/admin/stream`.** Six new SSE event kinds:
  `vector.collection.created`, `vector.collection.dropped`, `vector.replica.assigned`,
  `vector.replica.lost`, `vector.heal.started`, `vector.heal.completed`. Each event
  carries a monotonic `sequence`, an `atUtc` timestamp, the `collection`, and a `data`
  blob (holder connection id, node id, reason, before/after counts, records, lastSeq).
  Node snapshots still fire as before, so a single stream carries everything an
  operator needs.
- **New `VectorEvents` bus.** In-process fan-out with `DropOldest`-style bounded
  queues so a slow subscriber can never back-pressure the write path.
  `ReplicationCoordinator` and `HealingService` are the two publishers today.
- **Console — Vector collections panel.** New table with record count, replication
  factor (target vs current), replica placement per node, and a badge that flips to
  "under-replicated" when a collection drops below target. Each row has a Rebuild
  button that calls the admin `/rebuild` endpoint.
- **Console — Vector activity feed.** Rolling live feed of the last 40 vector events
  (newest first) with per-kind colour cues. Updates over SSE with the polling
  fallback the console already had for node snapshots.
- **`GET /api/admin/vector/collections/{collection}` (new).** Per-collection detail:
  collection info, placement, `underReplicated` flag, and per-collection query stats
  (`queries`, `queryLatencyAvgMs`). Admin-scoped; the console uses the list endpoint
  today but this is available for scripts and future console tabs.
- **Per-collection query stats.** `Metrics` gains `PerCollection` in its snapshot —
  a rolling count of queries and average latency in ms per collection. Recorded on
  every `/api/vector/{collection}/query` and `/retrieve` call regardless of whether
  the query was served hub-local or by a node replica.
- **README — Vector store section.** Full raw-store/replica model, config table for
  every `VectorStore:*` / `Vector:*` key, data-plane and admin-plane endpoint tables,
  SSE event kinds, and scaling / multi-coordinator notes. Phase table extended to
  include phases 13–17.

## Compatibility

- No breaking changes for callers. With `VectorStore:Enabled=false` (the default)
  `/api/status` returns `vector: null` and no vector SSE events ever fire; consumers
  that ignore the new field see nothing new.
- `MetricsSnapshot` gained a `PerCollection` field. Consumers using its constructor
  positionally need to add the new argument; property access by name is unchanged.
- `StatusEndpoint` DI signature widened by one parameter (`IServiceProvider`) — the
  endpoint mapping extension method's signature is unchanged.
- The console is still build-free static assets. No new toolchain, no framework.

## What's next

Phase 18 (`v2.0.0`) is the GA milestone: retrieval wired into `/api/chat` and
`/api/generate` via opt-in headers, with the hub orchestrating the pipeline
(embed → search → prompt assembly) and the nodes doing the heavy compute. That
closes the "RAG mesh" arc set out at the start of the track.
