# Running InferHub in Docker

A coordinator and a node, one command. The coordinator is the piece that genuinely wants to
be a container — it is stateless, always-on, and belongs on a small VPS. The GPU nodes mostly
do not; see [Where the nodes should actually run](#where-the-nodes-should-actually-run).

## Quick start

```bash
cp deploy/docker/.env.example deploy/docker/.env    # fill in the three keys
docker compose -f deploy/docker/docker-compose.yml up -d
```

Then, from the host:

```bash
export KEY=<your INFERHUB_API_KEY>

curl -H "Authorization: Bearer $KEY" http://localhost:5080/v1/models
curl -H "Authorization: Bearer $KEY" http://localhost:5080/api/tags
```

The admin console is at <http://localhost:5080/console>, the read-only status page at
<http://localhost:5080/status>, and `/health` is open to monitoring without a key.

## ⚠ In Docker there is no loopback exemption — set the keys or nothing gets through

On bare metal, InferHub skips authentication for loopback callers
(`Auth:RequireAuthForLoopback=false`, the default), which is why the `dotnet run` quickstart
lets you `curl localhost` with no key at all.

**That does not carry over to Docker.** A request from your host does not reach the
coordinator over loopback — it arrives over the bridge network, from the gateway address,
which is not a loopback source. The exemption never fires, so every call needs a real
`Authorization: Bearer` header.

This is the right default and we did not weaken it for containers. But it surprises people
coming from the bare-metal quickstart, so: **fill in `.env` first.** Compose is configured to
refuse to start rather than silently come up with an empty key list you can't authenticate
against.

The three scopes stay independent — an admin key is not an inference key:

| `.env` variable | Config key | Unlocks |
|---|---|---|
| `INFERHUB_API_KEY` | `Auth:ApiKeys` | `/api/*` and `/v1/*` — inference, tags, embeddings |
| `INFERHUB_ADMIN_KEY` | `Auth:AdminApiKeys` | `/api/admin/*` — cordon, drain, deregister |
| `INFERHUB_NODE_SECRET` | `Auth:NodeEnrollmentSecret` | the node's SignalR handshake |

## Pointing an OpenAI client at it

The base URL is the coordinator plus `/v1`:

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://localhost:5080/v1",
    api_key="<your INFERHUB_API_KEY>",
)

stream = client.chat.completions.create(
    model="llama3",
    messages=[{"role": "user", "content": "Explain NAT traversal in two sentences."}],
    stream=True,
)
for chunk in stream:
    print(chunk.choices[0].delta.content or "", end="")
```

Retrieval works through the same client — set the header once and every call is grounded:

```python
client = OpenAI(
    base_url="http://localhost:5080/v1",
    api_key="...",
    default_headers={"X-InferHub-Retrieve": "my-collection"},
)
```

## Where the nodes should actually run

The compose file ships a node container so `up -d` gives you something that works end to end.
It reaches Ollama on your host via `host.docker.internal` (Docker Desktop) or the bridge
gateway `172.17.0.1` (plain Linux) — set `OLLAMA_ENDPOINT` in `.env` accordingly.

There are three honest answers, and which is right depends on what is already on the box.

**1. Natively, next to a local Ollama.** Leanest, and still the recommendation for a Windows GPU
box — the phase-19 service host (`deploy/windows/`) gives you auto-start on boot and
restart-on-failure with nothing else installed.

**2. This node container, reaching an Ollama on the host.** What the compose file above does.
Note that the *node* does no compute in this shape — it shells out to Ollama over HTTP — so
passing a GPU into this particular container buys nothing.

**3. The bundled image, which has its own Ollama inside it (v3.7+).** One `docker run`, nothing
installed on the host, and a GPU worth passing in because now something in there uses it:

```bash
docker run -d --name inferhub --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -v inferhub:/data -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:ollama

docker exec inferhub ollama pull llama3.2
```

Leave `--gpus all` off and it runs on the CPU; set `Ollama__Supervisor__Enabled=false` and it is a
vector store with no inference process at all. It is ~4 GB against the plain image's ~340 MB,
amd64-only and NVIDIA-only, and the volume is required in practice, because pulled models live in
it. `:gpu` is an alias for the same image. See
[A node and its Ollama in one container](../../README.md#a-node-and-its-ollama-in-one-container-v37)
and `compose.ollama.yml` beside this file.

The shape most fleets land on is a containerized coordinator on a small always-on box plus nodes
on the GPU machines that dial out to it — native, or bundled, as above. Nothing about that needs
an inbound firewall rule on the node side:

```jsonc
// appsettings.json on the GPU box
{
  "Coordinator": {
    "Url": "https://hub.example.com/",
    "EnrollmentSecret": "<INFERHUB_NODE_SECRET>"
  },
  "Ollama": { "Endpoint": "http://localhost:11434/" }
}
```

To run the containerized coordinator without the bundled node at all:

```bash
docker compose -f deploy/docker/docker-compose.yml up -d coordinator
```

## The small sibling — a node on its own (v3.5+)

This whole stack exists to route between clients and a *fleet*. If you have one machine, solo
mode drops the coordinator entirely and the node serves the same API itself:

```bash
docker run -d --name inferhub-solo \
  -e LocalApi__Enabled=true \
  -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -e Ollama__Endpoint=http://host.docker.internal:11434/ \
  -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:latest
```

Then point any OpenAI client at `http://localhost:5081/v1` — same bodies, same responses, same
streaming as the hub. **The key is not optional here**, and for the same reason as the warning
at the top of this file: the container binds a wildcard, so the API is reachable from outside
the box, and a keyless inference endpoint hands arbitrary GPU time to whoever finds the port.
`LocalApi__AllowAnonymous=true` is the explicit override if that network is genuinely trusted.

Solo mode has no admin API and no console — see
[Solo mode](../../README.md#solo-mode--just-the-node-v35) for the full surface and what it
deliberately refuses.

### With its own corpus (v3.6+)

A standalone node can also ingest documents and ground its own answers. Two extra flags and a
volume:

```bash
docker run -d --name inferhub-solo \
  -e LocalApi__Enabled=true \
  -e Coordinator__Enabled=false \
  -e LocalApi__Retrieval__Enabled=true \
  -e LocalApi__Retrieval__DefaultEmbeddingModel=nomic-embed-text \
  -e LocalApi__ApiKeys__0="$INFERHUB_API_KEY" \
  -e Ollama__Endpoint=http://host.docker.internal:11434/ \
  -v inferhub-solo-data:/data \
  -p 5081:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:latest
```

**The volume is the part people forget.** The corpus is written to `/data/retrieval` inside the
container; without a volume it is destroyed with the container, and every document has to be
ingested again. The image sets the path (and `/data` is `chown`ed to the `app` user) precisely so
this works — but nothing can make an unmounted directory survive `docker rm`.

`LocalApi__Retrieval__Enabled=true` **requires** `Coordinator__Enabled=false`; with both on the
container refuses to start and says which key to change. That is deliberate: a meshed node already
holds replicas derived from its coordinator, and a second authoritative corpus in the same process
would be two sources of truth for the same collection names. PDF ingestion is not available on a
node (a clean `415`), and neither is the postgres or qdrant provider.

## Vector store

Disabled by default. Set `INFERHUB_VECTORS_ENABLED=true` in `.env` to turn on the local
file-backed provider; it persists to the `coordinator_vectors` named volume, so it survives
`docker compose down` and is lost on `docker compose down -v`.

For an external, durable store, layer the Postgres overlay on top — later files win, so the
base file's `local` provider is replaced by `postgres`:

```bash
docker compose -f deploy/docker/docker-compose.yml \
               -f deploy/docker/compose.postgres.yml up -d
```

That adds a `pgvector/pgvector:pg17` service and points the coordinator at it. Under
`postgres`, Postgres is the only source of truth: the coordinator holds no vector state on
disk and node replication is deliberately off, because a second derived copy on the fleet
would be a second write path.

## Observability — Prometheus + Grafana

The coordinator exposes `GET /metrics` in the Prometheus text exposition format. It measures
nothing new: the numbers were already on `/api/status` and the status page, and this gives
them a history and an alert.

The monitoring stack is an **overlay**, not part of the base — nobody who just wants a mesh
should have to run Prometheus to get one:

```bash
# 1. Give Prometheus the admin key, in a file so it never lands in a config you might paste.
echo -n "$INFERHUB_ADMIN_KEY" > deploy/docker/observability/inferhub-key

# 2. Bring up the overlay.
docker compose -f deploy/docker/docker-compose.yml \
               -f deploy/docker/compose.observability.yml up -d
```

Grafana is on `http://localhost:3000` (`admin` / `${GRAFANA_PASSWORD:-admin}`) with the
**InferHub mesh** dashboard already provisioned: fleet in-flight, nodes connected, request
rate by outcome, measured tokens/second per node and model, queue depth and outcomes,
retrieval latency per collection, and per-client token consumption.

**The scrape uses the *admin* key, not a client key.** `/metrics` is guarded by
`AdminApiKeyMiddleware` — a scraper is not an inference client, and handing a monitoring
system a token that can spend GPU time would be the wrong trade. On a trusted network you can
drop the guard with `Metrics__OpenScrape=true` and delete the `authorization` block from
`observability/prometheus.yml`; be deliberate about it, because the endpoint exposes node
names, model names and client ids.

Verify the endpoint by hand:

```bash
curl -H "Authorization: Bearer $INFERHUB_ADMIN_KEY" http://localhost:5080/metrics
```

## High availability — a warm standby coordinator

The always-on hub is the mesh's single durability anchor, which also makes it the single point
of failure. From v3.0 you can run a **second coordinator as a warm standby** over the same
Postgres. Exactly one holds a lease and serves inference; the other waits, answering `/health`,
`/api/status` and `/metrics` but returning `503` on every inference route. When the active one
dies, the standby takes the lease within the TTL, the nodes reconnect to it, and the mesh keeps
serving — no manual promotion, no data migration.

**This requires `VectorStore:Provider=postgres`**, because that is where the durable truth
already lives outside any one coordinator. The `local` provider's raw store is per-hub and
clustering it is future work. Usage persistence should be `postgres` too, or an invoice restarts
from zero on whichever hub happens to be leading.

```bash
docker compose -f deploy/docker/docker-compose.yml \
               -f deploy/docker/compose.postgres.yml \
               -f deploy/docker/compose.ha.yml up -d
```

That brings up `coordinator` (hub-a), `coordinator-standby` (hub-b), the shared Postgres, and an
nginx `front` on `http://localhost:5079` as the single client address. Both hubs keep their own
published ports (`5080` and `5081`) — a failover runbook you can only follow through the load
balancer is a runbook you cannot debug.

**Which one leads is a race, and the service names do not decide it.** `coordinator-standby` is
just the container that was added second; on a cold start either hub may take the lease first. Ask
`/health` rather than assuming.

**What is a standby, and what does a client see?**

| Signal | Active | Standby |
|---|---|---|
| `X-InferHub-Role` (every response) | `active` | `standby` |
| `GET /health` | `200`, `"role": "active"` | `200`, `"role": "standby"` |
| `POST /api/chat`, `/v1/*`, ingest, search | served | `503` + `Retry-After: 5` |
| `GET /api/status`, `/metrics`, `/api/admin/*` | served | served |
| Node SignalR handshake | accepted | refused with `503` |

A standby returning `200` on `/health` is deliberate: a standby **is** healthy, it just is not
leading. Reporting it unhealthy would have an orchestrator restart-loop the very instance that is
supposed to be waiting quietly. Drain it on the role field or on the inference `503`, not on
`/health`.

### Failover runbook

```bash
# Who is leading right now?
curl -s http://localhost:5080/health   # hub-a
curl -s http://localhost:5081/health   # hub-b

# Kill the active one.
docker compose -f deploy/docker/docker-compose.yml \
               -f deploy/docker/compose.postgres.yml \
               -f deploy/docker/compose.ha.yml stop coordinator

# Within Cluster__LeaseTtlSeconds (15s by default, and immediately on a *clean* stop, because a
# shutting-down active hub releases the lease rather than letting it expire):
curl -s http://localhost:5081/health          # -> "role": "standby" becomes "active"
curl -s http://localhost:5079/api/status      # through the front: served by hub-b
docker compose ... logs coordinator-standby   # "Promoted to ACTIVE coordinator"
```

The nodes rotate through `Coordinator__Endpoints` and re-register with whoever answers, so
`/api/nodes` on hub-b fills in over the next few seconds. Warm conversation affinity survives the
switch if `Affinity__Persistence=file` is on **and** both hubs share that directory; otherwise the
first turn after a failover costs one cold model load, which is the phase-30 contract.

**Tuning the TTL.** `Cluster__LeaseTtlSeconds` is the worst-case failover delay *and* the window
inside which a partitioned old primary must have fenced itself — they are the same number, on
purpose. `Cluster__RenewIntervalSeconds` must be at most a third of it (startup fails if it is not)
so one slow round-trip cannot demote a healthy leader.

**Split-brain.** A coordinator that cannot renew its lease demotes itself after one TTL, measured
on its own clock, whether or not it can reach anything to ask. So an unreachable database takes the
mesh down rather than letting two hubs both serve — correct, and not something to soften: an
inference request the mesh cannot attribute to a single leader is worse than a `503` the front
routes elsewhere. Watch `inferhub_cluster_active` on `/metrics`: summed across both hubs it should
be exactly `1`. `0` means no leader; `2` would mean the fence failed.

### What v3.0 does *not* do

Active-active load sharing (both hubs serving at once) and clustering the `local` vector provider
are explicitly future work. This is the foundation — a survivable hub — not the whole HA track.

## Images

Published to GHCR on every `v*` tag. All tags are under `ghcr.io/dev-art-solutions/`, and every
image also carries the minor (`:3.13`) and, for the two multi-arch ones, `:latest`.

| Pull this | Size | Arch | When it is the right one |
|---|---:|---|---|
| `inferhub-coordinator` | ~120 MB | amd64 + arm64 | The always-on host. No GPU and no inference engine — it routes, serves the client API and hosts the console. |
| `inferhub-node` | ~340 MB | amd64 + arm64 | You already run Ollama, vLLM, LM Studio or a hosted OpenAI-compatible endpoint. Also the right one for **solo mode** and for a **vector-store-only** box. |
| `inferhub-node:ollama` | ~4 GB | amd64 | One `docker run` on a GPU box with nothing installed on the host: Ollama runs inside the container, supervised by the node itself. `:gpu` is an alias of the same digest. |
| `inferhub-node:tools` | ~6 GB | amd64 | The above **plus speech** — Python, `faster-whisper` and `piper`, so `/v1/audio/*` works out of the box. |

### The three node shapes, and which image each wants

1. **A node in front of an engine you already run** — plain `inferhub-node`, `Backend:Type=ollama`
   pointed at your host's Ollama, or `Backend:Type=openai` pointed at vLLM/LM Studio/anything.
   340 MB, multi-arch, works on a Raspberry Pi.
2. **A self-contained GPU box** — `:ollama`. The node is PID 1 and Ollama is its child; the phase-36
   supervisor restarts it if it wedges. Pull models with `docker exec … ollama pull`.
3. **A self-contained GPU box that also does speech** — `:tools`. Same as above with the two Python
   workers and `Tools:AllowModelDownload=true`, so the first transcription fetches its weights onto
   the volume.

**Two mistakes the sizes make expensive.** Do not pull `:tools` for chat — it is `:ollama` plus
~1.5 GB of Python wheels nothing will load, and every image above does chat identically. Do not pull
`:ollama` to sit next to an Ollama you already run: the bundled one idles beside it, or fights it for
the port and the loser is whichever one's logs you are reading.

Every image runs as a non-root `app` user. The coordinator listens on `8080` inside the container and
is published on `${INFERHUB_PORT:-5080}` on the host. **Mount a volume at `/data` on any node
image** — model weights, the node's stable id, tool scratch and any corpus live there, and without it
every `docker run` re-downloads gigabytes. A volume mounted at a path the image does not contain is
created root-owned and the container cannot write it; `/data` exists and is `chown`ed in all of them,
which is why it is that path and not a subdirectory of it.

## Configuration

Any setting in `appsettings.json` is settable as an environment variable — `__` separates
sections, and list entries are indexed:

```yaml
environment:
  Auth__ApiKeys__0: "first-key"
  Auth__ApiKeys__1: "second-key"
  Router__AffinitySlidingMinutes: "30"
  Dispatcher__TimeoutSeconds: "600"
```

One exception worth knowing about, because it will bite anyone writing their own Dockerfile:
**`ASPNETCORE_URLS` does not work here.** `appsettings.json` pins
`"Urls": "http://localhost:5080"`, and that layer overrides the `ASPNETCORE_`-prefixed
provider — so the app would bind loopback inside the container and answer nobody. The image
sets the config key directly (`Urls=http://+:8080`) instead, which is layered after
`appsettings.json` and actually wins.

## Troubleshooting

**`401` on every call.** Expected if `.env` is empty — see the loopback warning above. Check
you are sending the *inference* key, not the admin key.

**`/v1/models` returns an empty list.** The coordinator is up but no node has registered, or
the node registered and Ollama has no models pulled. `docker compose logs node` and
`docker compose exec node printenv Ollama__Endpoint`.

**The node can't reach Ollama.** From the node container:
`docker compose exec node curl -s http://host.docker.internal:11434/api/tags`. On plain Linux
swap in `http://172.17.0.1:11434/` and confirm Ollama is listening on all interfaces
(`OLLAMA_HOST=0.0.0.0`), not just loopback on the host.

**The node can't reach the coordinator.** They share the `inferhub` network and the node
addresses it as `http://coordinator:8080/` — the *internal* port, not `INFERHUB_PORT`.

**Prometheus logs `is a directory` for `/etc/prometheus/inferhub-key`.** You brought the
overlay up before writing the key file, and Docker created a directory in its place. Remove
it, write the file, and recreate the container:
`rm -rf deploy/docker/observability/inferhub-key && echo -n "$INFERHUB_ADMIN_KEY" > deploy/docker/observability/inferhub-key`.

**Prometheus target is `down` with a `401`.** The key file has a trailing newline (use
`echo -n`), or you wrote the *inference* key instead of the admin key.

**Upgrading from ≤ 2.9: the node's volume moved.** It was `node_replicas:/data/vector-replicas`
and is now `node_data:/data`, so the node's stable id and its vector replicas share the one mount
point the image creates and owns. On first start after the upgrade the node takes a new id and the
coordinator re-pushes its replicas — both correct, both one-time. Reclaim the old volume with
`docker volume rm inferhub_node_replicas`.
