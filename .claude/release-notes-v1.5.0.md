InferHub now has a memory. v1.5.0 introduces a local vector store that runs on the
coordinator — embedded, file-backed, no external service to stand up. This is the
foundation of the vector mesh track (phases 13–18); the rest of the track builds on
the storage contract this release establishes.

## What's new

- **`IVectorStore` seam + `LocalVectorStore` implementation.** A small abstraction in
  the spirit of `IInferenceBackend`, with one default implementation that owns the
  raw store and the in-memory index on the coordinator. Off by default.
- **Collections.** Named namespaces with a fixed vector dimension and distance metric
  each. Create, drop, list, get stats via the admin plane.
- **Raw store as the source of truth.** Each collection writes upserts and deletes to a
  plain append-only `ops.jsonl` under `VectorStore:DataDirectory`. After
  `VectorStore:SnapshotEveryOps` ops a compacted `snapshot.jsonl` is written and the
  tail is truncated. Replay on startup loads the snapshot and applies the surviving
  ops — robust to a crash between snapshot rename and ops truncate.
- **In-memory flat (exact) index.** Cosine (pre-normalised), dot, and l2 metrics, all
  rebuilt from the raw store at boot. The original vectors are preserved on disk so
  snapshots remain operator-portable.
- **Data plane (client scope).** `POST /api/vector/{collection}/upsert`,
  `POST /api/vector/{collection}/query`, `GET /api/vector/{collection}/{id}`,
  `DELETE /api/vector/{collection}/{id}`.
- **Admin plane (admin scope).** `POST/GET /api/admin/vector/collections` and
  `DELETE /api/admin/vector/collections/{collection}`, audited the same way as the
  existing admin actions.
- **`VectorStore:*` config with `ValidateOnStart`.** Bad values (unknown distance,
  blank data directory, retrieval `MaxRecords` below `DefaultK`, unknown `OnMissing`)
  stop the host with a message naming the offending key. `VectorStore:Enabled`
  defaults to `false`, so nothing changes for existing setups until you switch it on.

## Compatibility

- `Enabled=false` (the default) keeps v1.4 behaviour identically: no data directory
  touched, no vector endpoints mounted, no persisted state of any kind.
- No new NuGet packages. The dependency surface is unchanged.
- CLAUDE.md design rule #4 is amended to record this as the first (and only)
  deliberately persisted state, scoped to `VectorStore:DataDirectory`.

## What's next

Phase 14 (`v1.6.0`) wires the embedding side: a node with an embedding model generates
vectors on demand, so callers can push and query by plain text. Phases 15–18 add
replication across nodes, self-healing, the console surface, and finally inline
retrieval on `/api/chat` and `/api/generate`.
