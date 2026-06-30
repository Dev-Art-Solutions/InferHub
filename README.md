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

**InferHub 1.4** ships a self-hosted, Ollama-compatible inference mesh with a live
management console: Bearer auth, sticky + least-busy routing, live streaming, pre-stream
failover, typed/validated node config, an admin API (cordon / drain / deregister), and a
browser console that updates over Server-Sent Events.

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

**What's next.** Phases 17–18 finish the RAG mesh on top of v1.8's self-healing
replicas: a console surface for collections, replica health and live healing
events (`v1.9.0`), and finally inline retrieval on `/api/chat` and `/api/generate`
for the `v2.0.0` GA. Beyond that, multi-coordinator clustering, persisted
affinity, richer audit trails, and additional inference backends (vLLM,
llama.cpp) remain on the table.

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
| `Node:MaxConcurrency` | `null` | Advisory in-flight cap reported to the coordinator (null = unbounded). |
| `Node:Labels` | `{}` | Free-form key/value pairs surfaced on `GET /api/nodes`. |
| `Node:Models:Include` | `[]` | Whitelist of model names to advertise (empty = all). |
| `Node:Models:Exclude` | `[]` | Names dropped before reporting. |
| `Backend:Type` | `ollama` | Inference backend selector. |
| `Ollama:Endpoint` | `http://localhost:11434/` | Local Ollama URL (absolute http/https). |

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
