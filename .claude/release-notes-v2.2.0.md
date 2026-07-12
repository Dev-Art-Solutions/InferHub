Bring your own vector database. v2.2.0 makes the coordinator's vector store
pluggable and ships the first alternative backend: **PostgreSQL + pgvector**. Point
InferHub at a Postgres you already run, flip `VectorStore:Provider` to `postgres`,
and you get HNSW-indexed ANN search, real transactions, ordinary database backups,
and shared access from other apps — with every endpoint, header, and client call
unchanged. The interesting part is what we took away: in postgres mode the
coordinator stops replicating the index onto the GPU fleet, because Postgres is
already the source of truth and two sources of truth is a bug. Embeddings and inline
retrieval still run on the nodes — the mesh is intact; only the storage engine
changed. Local, file-backed and self-healing stays the default.

## What's new

- **Pluggable vector backends behind `IVectorStore`.** `VectorStore:Provider`
  selects `local` (default, unchanged) or `postgres`. Wiring lives in one
  composition root (`AddInferHubVectorStore`) that both `Program.cs` and the DI-shape
  test go through, so the two can never drift.
- **`PostgresVectorStore` (`Npgsql` + `Pgvector`).** Table-per-collection over
  pgvector (`vector(N)` is dimension-typed), a registry table tracking dimension and
  distance, HNSW (or IVFFlat, or exact) ANN index per the collection's metric, and a
  GIN index for metadata filters. Score sign-conventions **match `FlatIndex` exactly**
  (`cosine`/`dot` higher-is-better, `l2` lower-is-better) — a cross-provider parity
  test pins identical ordering and scores within 1e-4 for all three metrics.
- **Replication off by design under postgres.** `ReplicationCoordinator`,
  `HealingService`, and the node-serving query router are simply not registered;
  `NullVectorQueryRouter` takes over reads, the store publishes the
  `vector.collection.created` / `.dropped` events itself, the rebuild admin endpoint
  returns a reasoned `409`, and the status `vector` block carries a `provider` tag
  with replica fields zeroed (no false "under-replicated" flags). A composition test
  guards that the mesh services stay decoupled from the interface.
- **Startup bootstrapper, fail-fast.** Ensures the `vector` extension and schema
  (opt-out), creates the registry table, warms the metadata cache, and refuses to
  start with a clear message naming `VectorStore:Postgres:ConnectionString` when the
  DB is unreachable or the extension is missing — rather than 500ing on every call.
- **Console & config.** The Collections panel shows a provider badge and disables
  Rebuild under postgres; `appsettings.json` documents both providers; the README and
  static site gain a "PostgreSQL + pgvector" section with the config table, schema
  shape, honest trade-offs, and a `docker compose` + `curl` walk-through.

## Compatibility

- **`Provider=local` (and an absent `VectorStore` section) is byte-for-byte v2.1.0.**
  Same files on disk, same replication, same self-healing, same endpoints.
- **Dependencies.** `Npgsql` + `Pgvector` are a deliberate, provider-scoped
  exception to "no new heavy dependencies" (recorded in `CLAUDE.md` rules 4 & 5):
  coordinator-only, never in `InferHub.Shared`/`InferHub.Node`, and no connection is
  opened unless `Enabled=true` **and** `Provider=postgres`.
- **No migration path between providers yet** — switching on a populated deployment
  means re-ingesting. The 2000-dimension pgvector ANN ceiling falls back to exact
  scan (logged), not a silent broken index.
- `dotnet test` green with **and** without a database: the Postgres integration and
  cross-provider parity tests are gated on `INFERHUB_TEST_POSTGRES` and skip visibly
  when unset (no Testcontainers dependency); run them against
  `deploy/postgres/docker-compose.yml`.

## What's next

With the `IVectorStore` seam now proven by a second implementation, the next
candidates are additional backends (Qdrant, SQL Server vector) and a migration
command between providers.

---

## Release / distribution checklist (for the maintainer)

1. Tag `v2.2.0`, cut the GitHub release from these notes.
2. Update the static site (`inferhub.devart.solutions` → new
   `#idocs_vector_postgres` section + config-table rows + changelog row).
3. Blog post (`blog.devart.solutions`, slug
   `inferhub-postgres-pgvector-connector`) — default to draft, confirm visibility
   with Iliya before publishing.
4. Facebook + X posts (draft copy in the phase-20 plan).
