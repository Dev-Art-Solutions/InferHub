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

InferHub is being built in the open, one phase at a time. Each phase ends with a tagged
release. The current focus and the road ahead live in the roadmap below; the detailed,
day-by-day build briefs live in `/plan/` (kept out of version control).

| Phase | Theme | Version |
|------:|-------|---------|
| 1 | Foundation & coordinator skeleton (done) | `v0.1.0` |
| 2 | Node ↔ coordinator link (done) | `v0.2.0` |
| 3 | Model discovery (pluggable backend) (done) | `v0.3.0` |
| 4 | Routing & blocking generation (done) | `v0.4.0` |
| 5 | End-to-end streaming (done) | `v0.5.0` |
| 6 | Authentication & security (done) | `v0.6.0` |
| 7 | Conversations & smart routing (done) | `v0.7.0` |
| 8 | Resilience, observability & 1.0 | `v1.0.0` |

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

**Coordinator — client API keys & node enrollment secret**

```bash
# Linux / macOS
export Auth__ApiKeys__0="sk-client-token-1"
export Auth__ApiKeys__1="sk-client-token-2"
export Auth__NodeEnrollmentSecret="shared-node-secret"

# Windows PowerShell
$env:Auth__ApiKeys__0 = "sk-client-token-1"
$env:Auth__NodeEnrollmentSecret = "shared-node-secret"
```

Or with user-secrets (development):

```bash
dotnet user-secrets --project src/InferHub.Coordinator set "Auth:ApiKeys:0" "sk-client-token-1"
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
require a token even for loopback. **Remote (non-loopback) requests always require a
valid token**, regardless of this setting.

**Open endpoints.** `/health` is unauthenticated so monitoring systems can poll it.

**Production.** Always run the coordinator behind HTTPS (a reverse proxy like Caddy /
nginx, or Kestrel TLS). Bearer tokens are sensitive — don't send them over plain HTTP.

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
