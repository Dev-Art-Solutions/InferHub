# InferHub

**A self-hosted, Ollama-compatible inference mesh.**
Run the gateway where you have no GPU. Run worker nodes where you do.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Built on OllamaClient](https://img.shields.io/badge/built%20on-OllamaClient-2b8a3e.svg)](https://github.com/Dev-Art-Solutions/OllamaClient)

---

## The problem

GPUs and servers rarely live in the same place. The machine that is always on — a small
VPS, a home server — usually has no GPU. The machines that *do* have a GPU — a desktop, a
gaming rig, a workstation — are often behind a home router and not reachable from outside.

InferHub closes that gap. A lightweight **coordinator** runs on the always-on, GPU-less
host and speaks a familiar Ollama-style API. The actual work runs on **nodes** that sit on
your GPU machines, reach *out* to the coordinator, and pull jobs down. No port forwarding,
no exposing your desktop to the internet.

In short: one stable address in front, a pool of GPUs behind it.

## How it works

```
                                  ┌──────────────────────────────┐
   client (Ollama-compatible)     │          Coordinator         │
   curl / app / IDE plugin  ─────▶│  Ollama-style HTTP API        │
        (Bearer token if remote)  │  /api/tags /api/generate      │
                                  │  /api/chat                    │
                                  │                               │
                                  │  SignalR node hub  ◀──────────┼──── persistent outbound
                                  └───────────────┬───────────────┘     connection from nodes
                                                  │
                         dispatch (model-aware)   │   stream tokens back
                          ┌───────────────────────┼───────────────────────┐
                          ▼                       ▼                       ▼
                   ┌────────────┐          ┌────────────┐          ┌────────────┐
                   │   Node A   │          │   Node B   │          │   Node C   │
                   │  + Ollama  │          │  + Ollama  │          │  + Ollama  │
                   │  (GPU)     │          │  (GPU)     │          │  (GPU)     │
                   └────────────┘          └────────────┘          └────────────┘
```

- **Coordinator** — an ASP.NET Core service. It exposes an Ollama-compatible HTTP API,
  authenticates remote callers with a Bearer token, keeps track of which nodes are online
  and which models each one holds, and routes every request to a capable node.
- **Node** — a small .NET worker. It opens a persistent outbound connection to the
  coordinator (so it works fine behind NAT), reports which models it can serve, then
  receives prompts, runs them against its local inference backend, and streams the result
  back.
- **Pluggable backends** — each node runs an LLM backend behind a small abstraction
  (`IInferenceBackend`). The first backend is **Ollama**, driven by our own
  [OllamaClient](https://github.com/Dev-Art-Solutions/OllamaClient) library. The mesh
  isn't tied to it — other backends (vLLM, llama.cpp, OpenAI-compatible servers) can slot
  in later without touching the coordinator.

Because the API is Ollama-shaped, existing Ollama clients, scripts, and editor plugins can
point at the coordinator and keep working — they just reach a whole pool of GPUs instead
of one.

## Status

**InferHub 2.0** closes the RAG mesh: models can now use the vector store inline.
Add `X-InferHub-Retrieve: <collection>` to a normal `/api/chat` or `/api/generate`
call and the coordinator embeds the query on a node, searches the collection (node
replica if available, hub-local otherwise), assembles an augmented prompt, and
dispatches the generation to a node — grounded answers with orchestration on the
hub and compute on the fleet. Retrieval is opt-in per request; a call without the
header behaves exactly like v1.x.

| Phase | Theme | Version |
|------:|-------|---------|
| 1 | Foundation & coordinator skeleton (done) | `v0.1.0` |
| 2 | Node ↔ coordinator link (done) | `v0.2.0` |
| 3 | Model discovery (pluggable backend) (done) | `v0.3.0` |
| 4 | Routing & blocking generation (done) | `v0.4.0` |
| 5 | End-to-end streaming (done) | `v0.5.0` |
| 6 | Authentication & security (done) | `v0.6.0` |
| 7 | Conversations & smart routing (done) | `v0.7.0` |
| 8 | Resilience, observability & 1.0 (done) | `v1.0.0` |
| 9 | Typed, validated node configuration (done) | `v1.1.0` |
| 10 | Coordinator admin API (done) | `v1.2.0` |
| 11 | Management console UI (done) | `v1.3.0` |
| 12 | Live updates & console hardening (done) | `v1.4.0` |
| 13 | Vector store foundation (done) | `v1.5.0` |
| 14 | Embeddings & retrieval (done) | `v1.6.0` |
| 15 | Replication across nodes (done) | `v1.7.0` |
| 16 | Durability & self-healing (done) | `v1.8.0` |
| 17 | Console & observability (done) | `v1.9.0` |
| 18 | Retrieval-augmented inference (done) | `v2.0.0` |
| 19 | PostgreSQL + pgvector connector (done) | `v2.2.0` |

**What's next.** With the `IVectorStore` seam now proven by a second implementation, the
next candidates are additional vector backends (Qdrant, SQL Server vector) and a migration
command between providers. Beyond storage, multi-coordinator clustering, persisted affinity,
richer audit trails, and additional inference backends (vLLM, llama.cpp) remain on the table.

## Quick start

```bash
# On the always-on host (no GPU needed)
dotnet run --project src/InferHub.Coordinator

# On each GPU machine (with Ollama already running locally)
dotnet run --project src/InferHub.Node

# From anywhere, talk to it like Ollama (remote calls need a Bearer token)
curl http://your-coordinator:5080/api/chat \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -d '{"model":"llama3","messages":[{"role":"user","content":"Hello!"}],"stream":false}'
```

## Authentication & configuration

InferHub keeps secrets out of source. Configure them at runtime via environment variables
or .NET user-secrets — `appsettings.json` only ships empty placeholders.

**Three independent token sets**

| Token set | Used by | Coordinator config key |
|---|---|---|
| Client API keys | Inference callers (`/api/generate`, `/api/chat`, `/api/tags`) | `Auth:ApiKeys` |
| Admin API keys | The management console & `/api/admin/*` (cordon/drain/deregister) | `Auth:AdminApiKeys` |
| Node enrollment secret | Worker nodes joining the SignalR hub | `Auth:NodeEnrollmentSecret` |

Client and admin scopes are checked separately — an admin key cannot run inference
unless it is also listed in `Auth:ApiKeys`, and vice versa.

**Coordinator — client, admin & enrollment secrets**

```bash
# Linux / macOS
export Auth__ApiKeys__0="sk-client-token-1"
export Auth__ApiKeys__1="sk-client-token-2"
export Auth__AdminApiKeys__0="sk-admin-token"
export Auth__NodeEnrollmentSecret="shared-node-secret"

# Windows PowerShell
$env:Auth__ApiKeys__0 = "sk-client-token-1"
$env:Auth__AdminApiKeys__0 = "sk-admin-token"
$env:Auth__NodeEnrollmentSecret = "shared-node-secret"
```

Or with user-secrets (development):

```bash
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:ApiKeys:0" "sk-client-token-1"
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:AdminApiKeys:0" "sk-admin-token"
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:NodeEnrollmentSecret" "shared-node-secret"
```

**Node — enrollment secret**

```bash
export Coordinator__EnrollmentSecret="shared-node-secret"
# or
dotnet user-secrets --project src/InferHub.Node set "Coordinator:EnrollmentSecret" "shared-node-secret"
```

**Loopback policy.** By default, requests originating from `127.0.0.1` / `::1` skip the
Bearer-token check — handy for local testing. Set `Auth__RequireAuthForLoopback=true` to
require a token even for loopback. The same switch applies to both inference and admin
routes. **Remote (non-loopback) requests always require a valid token**, regardless of
this setting.

**Open endpoints.** `/health` is unauthenticated so monitoring systems can poll it.

**Production.** Always run the coordinator behind HTTPS (a reverse proxy like Caddy /
nginx, or Kestrel TLS). Bearer tokens are sensitive — don't send them over plain HTTP.

### All configuration keys

Every coordinator setting lives in `appsettings.json` (or overridden via env vars / user
secrets). Defaults are listed below — sensible for a single-host deployment.

| Key | Default | Purpose |
|---|---|---|
| `Urls` | `http://localhost:5080` | Address the coordinator listens on. |
| `NodeRegistry:TimeoutSeconds` | `30` | Heartbeat-miss window before a node is evicted and its in-flight jobs are failed. |
| `NodeRegistry:ReaperIntervalSeconds` | `5` | How often the reaper sweeps for stale nodes. |
| `Dispatcher:TimeoutSeconds` | `300` | Per-job wall-clock timeout (applies to streaming and blocking). |
| `Router:AffinitySlidingMinutes` | `10` | Sticky-conversation idle expiry. |
| `Router:AffinityLoadBreakThreshold` | `2` | Extra in-flight jobs the sticky node may have before affinity is broken in favour of a less-busy node. |
| `Auth:ApiKeys` | `[]` | Accepted client Bearer tokens (constant-time compared). |
| `Auth:AdminApiKeys` | `[]` | Accepted admin Bearer tokens guarding `/api/admin/*`. Separate from `ApiKeys`. |
| `Auth:RequireAuthForLoopback` | `false` | Force loopback callers to present a token too (applies to client and admin scopes). |
| `Auth:NodeEnrollmentSecret` | _(empty)_ | Shared secret nodes present when joining the hub. Empty disables enrollment. |

### Node configuration

Every node setting lives in `src/InferHub.Node/appsettings.json` and is validated at startup
— a bad value (non-URL coordinator, negative interval, `MaxConcurrency < 1`) stops the
process with a message naming the offending key. Env-var / user-secrets overrides work as
usual (`Coordinator__EnrollmentSecret`, `Node__Name`, etc.).

| Key | Default | Purpose |
|---|---|---|
| `Coordinator:Url` | `http://localhost:5080/` | Coordinator base URL (must be absolute http/https). |
| `Coordinator:EnrollmentSecret` | _(empty)_ | Shared secret matching the coordinator's `Auth:NodeEnrollmentSecret`. |
| `Coordinator:HeartbeatInterval` | `00:00:10` | How often the node pings the coordinator. |
| `Coordinator:ModelRefreshInterval` | `00:01:00` | How often the node re-reports its model list. |
| `Coordinator:RetryDelay` | `00:00:05` | Wait between reconnect attempts. |
| `Node:Name` | _(machine name)_ | Friendly node name shown in the status page. |
| `Node:DataDirectory` | `null` | Directory for writable node state (the `.inferhub-node-id` file). `null` = next to the executable (content root). Set to e.g. `C:\ProgramData\InferHub\Node` when running as a Windows service under a least-privilege account. |
| `Node:MaxConcurrency` | `null` | Advisory in-flight cap reported to the coordinator (null = unbounded). |
| `Node:Labels` | `{}` | Free-form key/value pairs surfaced on `GET /api/nodes`. |
| `Node:Models:Include` | `[]` | Whitelist of model names to advertise (empty = all). |
| `Node:Models:Exclude` | `[]` | Names dropped before reporting. |
| `Backend:Type` | `ollama` | Inference backend selector. |
| `Ollama:Endpoint` | `http://localhost:11434/` | Local Ollama URL (absolute http/https). |

### Running a node as a Windows service

For an always-on GPU box, run the node as a native Windows service — auto-start on boot,
restart-on-failure recovery, and logging to the Windows Event Log. The service host
(`src/InferHub.Node.WindowsService`) is a thin wrapper that composes the exact same node
services through the shared `AddInferHubNode` root, so it behaves identically to
`dotnet run --project src/InferHub.Node`; only the packaging differs. Dev/console and
Linux node paths are unchanged.

```powershell
# 1. Publish a self-contained single-file host (no .NET runtime needed on the box)
dotnet publish src/InferHub.Node.WindowsService -c Release -r win-x64

# 2. Copy the publish output to C:\Program Files\InferHub\Node, then set the coordinator
#    URL in appsettings.json and the enrollment secret as a machine env var
[Environment]::SetEnvironmentVariable('Coordinator__EnrollmentSecret','shared-node-secret','Machine')

# 3. Install + start the service (run elevated)
./deploy/windows/install-service.ps1 `
  -BinaryPath "C:\Program Files\InferHub\Node\InferHub.Node.Service.exe" -DelayedStart
```

The install script configures automatic (or delayed-auto) start, restart-on-failure
recovery, and a writable data directory (`Node:DataDirectory`, default
`C:\ProgramData\InferHub\Node`) so the node identity file survives under a least-privilege
account. Full runbook — including update/uninstall and virtual-account setup — is in
[deploy/windows/README.md](deploy/windows/README.md).

> The Linux equivalent is the same host pattern with `builder.Services.AddSystemd()` and a
> `.service` unit file — same composition root, different lifetime integration.

## Vector store

InferHub's vector store is **provider-backed** and off by default — flip
`VectorStore:Enabled` to turn it on, and pick a backend with `VectorStore:Provider`:

- **`local`** (default) — embedded and file-backed in the coordinator, replicated to the GPU
  fleet and self-healing. Zero external services; plain-file backups.
- **`postgres`** (v2.2+) — an external **PostgreSQL + pgvector** database: HNSW-indexed ANN
  search, real transactions, ordinary database backups, and shared access from other apps.
  See [PostgreSQL + pgvector](#postgresql--pgvector-v22) below.

Every endpoint, header, and client call is identical across providers. Embeddings and inline
retrieval always run on the fleet — only the storage engine changes.

### Local provider

The local store is embedded and file-backed. Two layers:

- **Raw store** — an append-only op log (upserts + tombstones) plus periodic compacted
  snapshots, one directory per collection under `VectorStore:DataDirectory`. Plain files
  an operator can copy. This is the **source of truth**.
- **Index** — a queryable structure built from the raw store. In-memory on the hub;
  replicated to node holders (see below). Rebuildable at any time from the raw store.

**Replication & self-healing.** When more than one node is online, each collection is
replicated across `VectorStore:ReplicationFactor` holders (capped at the connected node
count). The coordinator pushes the initial snapshot and forwards subsequent ops over the
existing SignalR link. If a holder drops, the healing loop re-pushes from the raw store to
restore the factor; if the **last** holder drops, the hub-local index keeps answering
reads and the next eligible node is seeded from raw. Node replicas are derived and
disposable — the hub's raw store is the durability anchor.

**Where work happens.** Coordinator orchestrates (owns the raw store, places replicas,
routes queries, heals); nodes compute (embedding + generation on the GPU). Vector search
runs hub-local by default, or on a node replica when one exists.

**Data-plane endpoints** (client scope, `Auth:ApiKeys`):

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/vector/{collection}/upsert` | Upsert a record. Accepts a raw `vector` or a `text` field (embedded on a node). |
| `POST` | `/api/vector/{collection}/query` | Top-k search. Same body shape as upsert — raw `vector` or `text`. |
| `POST` | `/api/vector/{collection}/retrieve` | Convenience RAG read (text → embed → search → matches). |
| `GET`  | `/api/vector/{collection}/{id}` | Fetch a single record. |
| `DELETE` | `/api/vector/{collection}/{id}` | Tombstone a record. |
| `POST` | `/api/embed` (+ `/api/embeddings`) | Drop-in Ollama-shaped embeddings endpoint (independent of the store). |

**Admin-plane endpoints** (admin scope, `Auth:AdminApiKeys`, audited):

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/api/admin/vector/collections` | List collections + placement (holders per collection). |
| `GET`  | `/api/admin/vector/collections/{collection}` | Detail: collection info, placement, under-replicated flag, per-collection query stats. |
| `POST` | `/api/admin/vector/collections` | Create a collection (`{ "name", "dimension", "distance"? }`). |
| `DELETE` | `/api/admin/vector/collections/{collection}` | Drop a collection. |
| `POST` | `/api/admin/vector/collections/{collection}/rebuild` | Force a heal pass — re-push from the raw store to restore the factor. Returns `409` under the postgres provider (nothing to re-push). |

**Live events.** `GET /api/admin/stream` (the same SSE stream used by the console) now
carries vector lifecycle events alongside node snapshots: `vector.collection.created`,
`vector.collection.dropped`, `vector.replica.assigned`, `vector.replica.lost`,
`vector.heal.started`, `vector.heal.completed`. Each event carries a monotonic
`sequence` and a `data` blob (holder connection id, node id, reason, before/after counts,
etc.). The management console renders these in the "Vector activity" feed.

**Status JSON.** `GET /api/status` grows a `vector` block when the store is enabled — a
`provider` tag (`local` \| `postgres`) plus per-collection record count, dimension, distance,
target vs live replicas, holder node ids, and an `underReplicated` flag. Metrics gains
`vectorReplicasHealed` / `vectorRebuildsFromRaw` / `vectorUnderReplicated` counters plus
`perCollection` query stats (`queries`, `queryLatencyAvgMs`). Under postgres the replica
fields are zeroed and the three heal/replica counters stay flat — there are no node replicas
to count.

### PostgreSQL + pgvector (v2.2+)

Set `VectorStore:Provider` to `postgres` and point the coordinator at a PostgreSQL that has
the [pgvector](https://github.com/pgvector/pgvector) extension. **Pick it when** you already
run Postgres, you want ANN search + transactions + ordinary backups + other apps reading the
same table, or the dataset has outgrown flat-exact search. **Stay local when** you want zero
external services, plain-file backups, and search replicas living on the GPU fleet.

**Schema.** One registry table (`{schema}.collections`) plus one table per collection,
`{schema}.{prefix}{collection}`, with `id text`, `embedding vector(N)`, `payload jsonb`,
`metadata jsonb`, `seq_no bigint`, `updated_at`. A pgvector ANN index (HNSW by default) is
built per the collection's distance metric, and a GIN index backs metadata filters. Score
sign-conventions match the local provider exactly (`cosine`/`dot` higher-is-better,
`l2` lower-is-better), so clients see identical rankings and numbers.

**Honest trade-offs.** Postgres owns durability, so under this provider:

- **no node replication, no self-healing, no node-served vector reads** — search runs in
  Postgres, and the coordinator holds no vector state on disk;
- the **rebuild** admin endpoint returns `409` (nothing to re-push);
- the `vectorReplicasHealed` / `vectorRebuildsFromRaw` / `vectorUnderReplicated` metrics stay
  at zero, and the status `vector` block zeroes the replica fields;
- pgvector's ANN index tops out at **2000 dimensions** — above that, the collection still
  works but falls back to exact scan (logged at creation).

The mesh is intact: **embeddings and inline retrieval still run on the GPU nodes** — only the
storage engine changed.

**No migration path yet.** Switching providers on a populated deployment means re-ingesting;
there is no built-in copy between `local` and `postgres`. Don't flip the switch expecting your
data to follow.

**Walk-through** (compose stack in [`deploy/postgres/`](deploy/postgres/docker-compose.yml)):

```bash
# 1. A Postgres with pgvector
docker compose -f deploy/postgres/docker-compose.yml up -d

# 2. Point the coordinator at it (env, not appsettings.json) and enable postgres
export VectorStore__Enabled=true
export VectorStore__Provider=postgres
export VectorStore__Postgres__ConnectionString="Host=localhost;Database=inferhub;Username=inferhub;Password=inferhub"
dotnet run --project src/InferHub.Coordinator

# 3. Same API as ever — create, upsert, query
curl -X POST http://localhost:5080/api/admin/vector/collections \
  -d '{"name":"docs","dimension":3,"distance":"cosine"}'
curl -X POST http://localhost:5080/api/vector/docs/upsert \
  -d '{"id":"a","vector":[1,0,0],"metadata":{"lang":"en"}}'
curl -X POST http://localhost:5080/api/vector/docs/query \
  -d '{"vector":[1,0,0],"k":3}'
```

### Retrieval-augmented inference

`/api/chat` and `/api/generate` accept optional headers that opt a normal request
into retrieval. Without a header the request is byte-for-byte unchanged — same body,
same routing, same streaming contract.

| Header | Purpose |
|---|---|
| `X-InferHub-Retrieve` | Collection name to retrieve from. Presence enables RAG. |
| `X-InferHub-Retrieve-K` | Top-k override (clamped to `VectorStore:Retrieval:MaxRecords`). |
| `X-InferHub-Retrieve-Model` | Embedding model override (defaults to `VectorStore:DefaultEmbeddingModel`). |

The coordinator extracts the query text (last user message for chat; `prompt` for
generate), dispatches an embed job to a node, searches the collection (node replica
if available, hub-local otherwise), assembles an augmented prompt via
`VectorStore:Retrieval:Template` (the literal `{context}` placeholder is replaced by
the retrieved records rendered as `[id] text` — text is drawn from `payload.text`,
then `payload.content`, then the raw payload), and dispatches the generation to a
node. The response is Ollama-shaped and unaltered; the retrieved source ids come
back as a JSON array in `X-InferHub-Sources`.

**Where work happens.** Only orchestration and hub-local search live on the
coordinator. Embedding and generation are both dispatched to nodes — the mesh does
the heavy compute, exactly as with a bare `/api/chat` call. Rule #7 stays intact:
the augmented request body is assembled in-flight and forgotten.

**Failover.** Pre-stream failover still covers the generation job. A failed embed
or search surfaces via `VectorStore:Retrieval:OnMissing`: `error` returns
`424 Failed Dependency` with a message; `passthrough` runs the original request
unchanged and omits the sources header.

Example — a chat call grounded in the `docs` collection:

```bash
curl http://your-coordinator:5080/api/chat \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "X-InferHub-Retrieve: docs" \
  -H "X-InferHub-Retrieve-K: 4" \
  -d '{"model":"llama3","messages":[{"role":"user","content":"What is InferHub?"}],"stream":false}'
```

**Not yet.** Re-ranking, multi-collection fusion, query rewriting, and
tool-calling-style retrieval are separate tracks — future work.

### Vector configuration

Coordinator keys (all under `VectorStore:`):

| Key | Default | Purpose |
|---|---|---|
| `VectorStore:Enabled` | `false` | Master switch. Off = no persisted state, old contract. |
| `VectorStore:Provider` | `local` | Backend: `local` (file-backed, replicated) \| `postgres` (external pgvector). **(v2.2+)** |
| `VectorStore:DataDirectory` | `./data/vectors` | Raw store + snapshots on the coordinator. Local provider only. |
| `VectorStore:Distance` | `cosine` | Default similarity metric (`cosine` \| `dot` \| `l2`). |
| `VectorStore:ReplicationFactor` | `2` | Target node replicas per collection (capped at connected node count). Local provider only. |
| `VectorStore:DefaultEmbeddingModel` | `nomic-embed-text` | Model used when a text upsert/query omits one. |
| `VectorStore:SnapshotEveryOps` | `5000` | Ops appended before a compacted snapshot is written. |
| `VectorStore:Retrieval:DefaultK` | `4` | Top-k when a request opts into retrieval. |
| `VectorStore:Retrieval:MaxRecords` | `8` | Hard cap on injected records per request. |
| `VectorStore:Retrieval:OnMissing` | `error` | `error` \| `passthrough` when retrieval can't run. |
| `VectorStore:Retrieval:Template` | _(see below)_ | Prompt template applied to retrieved context; must contain `{context}`. |
| `VectorStore:Healing:DebounceMilliseconds` | `750` | Debounce for fleet-change-driven heal passes. |
| `VectorStore:Healing:IdleSweepSeconds` | `15` | Idle interval refreshing the under-replicated gauge. |

Postgres provider keys (all under `VectorStore:Postgres:`, used only when `Provider=postgres`) **(v2.2+)**:

| Key | Default | Purpose |
|---|---|---|
| `VectorStore:Postgres:ConnectionString` | _(empty)_ | Npgsql connection string. **Required.** Set via env (`VectorStore__Postgres__ConnectionString`) or user-secrets — never commit it. |
| `VectorStore:Postgres:Schema` | `inferhub` | Schema holding the registry and per-collection tables (`^[a-z_][a-z0-9_]*$`). |
| `VectorStore:Postgres:TablePrefix` | `vec_` | Prefix for per-collection tables (`^[a-z_][a-z0-9_]*$`). |
| `VectorStore:Postgres:AutoCreateExtension` | `true` | Run `CREATE EXTENSION IF NOT EXISTS vector` at startup. Set `false` if a DBA pre-installed it. |
| `VectorStore:Postgres:AutoCreateSchema` | `true` | Run `CREATE SCHEMA IF NOT EXISTS` at startup. |
| `VectorStore:Postgres:Index` | `hnsw` | ANN index: `hnsw` \| `ivfflat` \| `none` (exact scan). |
| `VectorStore:Postgres:HnswM` | `16` | HNSW `m` build parameter. |
| `VectorStore:Postgres:HnswEfConstruction` | `64` | HNSW `ef_construction` build parameter. |
| `VectorStore:Postgres:EfSearch` | `40` | Per-query `hnsw.ef_search` (higher = better recall, slower). |
| `VectorStore:Postgres:CommandTimeoutSeconds` | `30` | Npgsql command timeout. |
| `VectorStore:Postgres:MaxPoolSize` | `20` | Max pool size, applied if the connection string doesn't set one. |

Node keys (all under `Vector:`, only used when the node holds a replica — local provider):

| Key | Default | Purpose |
|---|---|---|
| `Vector:ReplicaDirectory` | `./data/vector-replicas` | Where a node persists assigned replicas so a restart doesn't require a full re-push. |

**Scaling note.** The local index is a flat (exact) cosine/dot/l2 search — small,
zero-dependency, correct. When a dataset outgrows flat exact, the `IVectorStore` seam is where
an approximate-nearest-neighbour strategy plugs in — and as of v2.2 that seam is **proven by a
second implementation**: the `postgres` provider serves HNSW-indexed ANN search from pgvector.
The dependency was a conscious, provider-scoped decision, not smuggled in.

**Multi-coordinator note.** The always-on hub is the single durability anchor by design
today. Cross-hub raw-store replication is future work and belongs to the "multi-coordinator
clustering" track called out below.

## Status & observability

A read-only status page lives at `/` (and `/status`). It auto-refreshes and shows the
fleet — connected nodes, their reported models, live in-flight counts, eviction history,
and failover stats. For fleet operations (cordon, drain, deregister) use the
[management console](#management-console--admin-api) at `/console`.

`GET /api/status` returns the same data as JSON:

```json
{
  "coordinatorVersion": "1.0.0",
  "nowUtc": "2026-06-18T12:00:00+00:00",
  "uptimeSeconds": 4321.5,
  "nodes": [ { "nodeId": "...", "name": "...", "inFlight": 0, "localInFlight": 0, "modelCount": 4, "ageSeconds": 1.2 } ],
  "models": [ { "name": "llama3", "digest": "...", "size": 4661211808 } ],
  "metrics": {
    "requestsTotal": 142, "requestsInFlight": 1, "requestsCompleted": 138,
    "requestsFailed": 3, "failoversAttempted": 2, "failoversSucceeded": 2,
    "nodesEvicted": 1, "perNode": [ … ]
  }
}
```

`GET /health` stays unauthenticated for monitoring.

## Management console & admin API

A browser console at `/console` (alias for `/console.html`) lets an operator drive the
fleet — not just watch it. It is built from the same dark-theme HTML/CSS/JS as the status
page (no build toolchain, no React) and uses the same admin endpoints any script can call.

**What you can do**

- **Cordon / Uncordon** — flip a node out of (or back into) the routing pool. A cordoned
  node finishes its in-flight jobs and refuses new ones; the router silently skips it
  when picking candidates.
- **Drain** — cordon, then wait for the node's local in-flight count to reach zero. The
  console implements this client-side as cordon + poll, so the request stays fast and the
  server never holds a long-lived connection.
- **Deregister** — force-disconnect a node and drop it from the registry. If the worker
  process is still running it will reconnect cleanly and re-register.
- **Live updates** — the console subscribes to `GET /api/admin/stream`
  (Server-Sent Events) and reflects node connect / disconnect / cordon changes in ~1s
  without a refresh. If the stream drops it transparently falls back to polling.
- **Last-action audit** — each row shows the most recent admin action (who, when), kept
  in memory by the coordinator.

**Admin endpoints (under `/api/admin`, scoped to `Auth:AdminApiKeys`)**

| Method | Path | Purpose |
|---|---|---|
| `GET`  | `/api/admin/nodes` | Richer node snapshot: cordon state, labels, max-concurrency, last action. |
| `GET`  | `/api/admin/stream` | SSE stream of fleet changes; keepalive every ~10s. |
| `POST` | `/api/admin/nodes/{nodeId}/cordon` | Stop routing new jobs to this node. |
| `POST` | `/api/admin/nodes/{nodeId}/uncordon` | Restore the node to the routing pool. |
| `POST` | `/api/admin/nodes/{nodeId}/deregister` | Force-disconnect and drop the node. |

Example — cordon a node from the CLI:

```bash
curl -X POST http://your-coordinator:5080/api/admin/nodes/<nodeId>/cordon \
  -H "Authorization: Bearer YOUR_ADMIN_KEY"
```

**Admin key handling in the browser.** The console prompts for the admin key once per
tab, keeps it in a JS variable for the session only — **never** in `localStorage` or
`sessionStorage` — and sends it as `Authorization: Bearer …` on admin calls. A 401
re-prompts; read-only stats keep rendering either way.

## Resilience & failover

InferHub is built to keep running while nodes come and go.

- **Heartbeat eviction.** Nodes that miss heartbeats past `NodeRegistry:TimeoutSeconds`
  are dropped from the registry; their in-flight jobs fail with a clear error and their
  sticky-conversation mappings are cleared.
- **Pre-stream failover.** If the chosen node drops *before* the first chunk is
  produced, the coordinator transparently retries the request on another capable node
  (when one exists). This applies to both blocking and streaming calls.
- **No silent retry mid-stream.** Once chunks have started flowing, the coordinator does
  **not** retry — the client already has partial output. Instead the stream ends with a
  final error chunk so callers don't hang.
- **Job timeout.** `Dispatcher:TimeoutSeconds` caps how long any one job can hold a
  request open.

## Conversations & routing

InferHub stores **no conversation content**. Clients send the full message history on every
turn, exactly like Ollama — the coordinator only decides *which node* runs the work.

- **Least-busy by default.** When several nodes hold the requested model, the coordinator
  picks the one with the lowest in-flight job count and breaks ties round-robin.
- **Sticky conversations.** Successive turns of the same chat prefer the same node, which
  keeps that model's KV-cache warm. Affinity expires after ~10 idle minutes.
- **Affinity guard.** If the sticky node is far busier than the least-busy alternative,
  the coordinator breaks affinity to avoid piling up on one machine.
- **Graceful fallback.** If the sticky node disconnects, the next turn transparently
  routes to another capable node and the mapping is refreshed.

**Tagging a conversation.** By default the coordinator hashes the opening system/user
message to detect a continuing thread without any client cooperation. Clients that want
explicit control can attach a stable id to every turn:

```bash
curl http://your-coordinator:5080/api/chat \
  -H "Authorization: Bearer YOUR_API_KEY" \
  -H "X-InferHub-Conversation: my-chat-7f3a" \
  -d '{"model":"llama3","messages":[...],"stream":false}'
```

The header value is opaque — any stable string identifying the conversation works.

## Built with

- [.NET 10](https://dotnet.microsoft.com/) (C#)
- [ASP.NET Core](https://learn.microsoft.com/aspnet/core/) + [SignalR](https://learn.microsoft.com/aspnet/core/signalr/) for the coordinator and the node link
- A pluggable node backend (`IInferenceBackend`); the first one is **Ollama**, via
  [OllamaClient](https://github.com/Dev-Art-Solutions/OllamaClient)
- [Ollama](https://github.com/ollama/ollama) on each node (for the Ollama backend)

## License

MIT — see [LICENSE](LICENSE).

---

Made by [Dev Art Solutions](https://devart.solutions). We build production-ready AI and
agent systems. Say hello: hello@devart.solutions
