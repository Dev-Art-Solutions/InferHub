# InferHub v3.1.0 — a third vector backend: Qdrant

The `IVectorStore` seam gains its third implementation, [Qdrant](https://qdrant.tech), alongside the
file-backed `local` store and `postgres`. Turn it on with two config keys:

```jsonc
"VectorStore": { "Enabled": true, "Provider": "qdrant", "Qdrant": { "Url": "http://localhost:6333" } }
```

Every endpoint, header and client call is identical to the other providers, proven by a parity test
that runs one dataset through all three engines and fails if any of them disagrees on ordering or
scores by more than a rounding error.

## Zero new dependencies — the actual headline

The pgvector connector (v2.2) cost two packages, `Npgsql` and `Pgvector`. **Qdrant cost none.** Its
API is plain JSON over HTTP, and the house already speaks HTTP-to-a-server by hand — the OpenAI
upstream client from v2.4 is the precedent. So
[`QdrantClient`](../src/InferHub.Coordinator/Vector/Qdrant/QdrantClient.cs) is a hand-rolled
`HttpClient` connector. The official Qdrant client is gRPC and would have dragged `Grpc.Net.Client`
and protobuf into the coordinator for a store that answers REST perfectly well; it was considered and
declined. A third vector backend, and a clean `git diff` on every `.csproj`.

## The one real gotcha: ids

Qdrant accepts **only** an unsigned integer or a UUID as a point id, and InferHub ids are neither — a
chunk id is a SHA-256 of its document and index, a document id might be a filename. So every id is
hashed into a deterministic `UUIDv5`
([`QdrantIdMap`](../src/InferHub.Coordinator/Vector/Qdrant/QdrantIdMap.cs)) and the real id — with the
payload and metadata — rides in the point payload under reserved `__*` keys. Because the UUID is
deterministic, re-ingesting a document still **replaces** its chunks rather than duplicating them, the
idempotent-re-ingest promise from v2.5. Nothing above the store ever sees the UUID; queries and scans
return the real id, read back out of the payload.

## Same architecture as Postgres — external provider

Qdrant is an external, durable, concurrently-queryable store, so — exactly as under `postgres`:

- **no node replication, no self-healing, no node-served vector reads**; the store publishes its own
  `vector.collection.created` / `.dropped` lifecycle events;
- the **rebuild** admin endpoint returns `409`, the replica metrics stay zero, and the status `vector`
  block reports `"provider":"qdrant"` with zeroed replica fields.

Internally, the one predicate every call site branches on is now `IsExternal` (postgres **or** qdrant),
not `IsPostgres` — what matters is external-vs-local, not which external one. The mesh is intact:
**embeddings and inline retrieval still run on the GPU fleet.** Only the storage engine changed.

## Honest limitation: keyword search is coarse this release

Qdrant's full-text index is a filter, not a ranking. So keyword search scrolls a bounded slice of the
collection and ranks by term-overlap — enough to give hybrid retrieval a real second branch, but
explicitly not BM25, and the pipeline logs that it is coarse under Qdrant rather than pretending
otherwise. Server-side sparse-vector hybrid is the next release (v3.2). Vector search and inline RAG
are full-strength today.

## No migration path yet

As with Postgres, switching providers on a populated deployment means re-ingesting — there is no
built-in copy between `local`, `postgres` and `qdrant`. A cross-provider migration tool is planned for
v3.3. Don't flip the switch expecting your data to follow.

## Tests

635 passed, 35 skipped (the gated Postgres **and** Qdrant integration suites, which run only with
`INFERHUB_TEST_POSTGRES` / `INFERHUB_TEST_QDRANT` set). New coverage:

- `QdrantIdMapTests` — UUIDv5 determinism, canonical form, and no collisions over 20k distinct ids;
  the distance-enum mapping (`cosine→Cosine`, `dot→Dot`, `l2→Euclid`).
- `QdrantClientTests` — the exact JSON the connector sends (create, search, scroll, delete-by-filter,
  count), the `api-key` header, `404`→null, and non-2xx → `QdrantException` carrying the status. No
  server, via a stub handler.
- `QdrantVectorStoreTests` (gated) — CRUD, replace-by-id, dimension mismatch, delete semantics,
  filter narrowing, null-metadata exclusion, id-ordered scan with an exclusive cursor, filtered
  delete count, and a **local-vs-qdrant parity arm** asserting identical ordering and scores within
  `1e-4` for all three distances.
- `VectorCompositionTests` — under `qdrant`, `IVectorStore` is `QdrantVectorStore`, the router is
  `NullVectorQueryRouter`, and no `LocalVectorStore` / `ReplicationCoordinator` / `HealingService` is
  registered.
- `VectorStoreOptionsValidatorTests` — `qdrant` without a `Url` fails startup; a valid config passes;
  the unknown-provider message names `qdrant`.

## Zero new dependencies

Rule 5 holds for a third vector backend: `HttpClient` and `System.Text.Json` did all of it.
