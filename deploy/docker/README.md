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

For anything real, **run the node natively on the GPU box instead.** It wants to sit next to a
local Ollama with direct access to the GPU, and passing a GPU into a container buys you
nothing here because the node itself does no compute — it shells out to Ollama. On Windows,
the phase-19 service host is the supported path (`deploy/windows/`).

The shape most deployments land on is a containerized coordinator on a small always-on box
plus native nodes on the GPU machines that dial out to it. Nothing about that needs an inbound
firewall rule on the node side:

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

## Images

Published to GHCR on every `v*` tag, for `linux/amd64` and `linux/arm64`:

```
ghcr.io/dev-art-solutions/inferhub-coordinator:2.3.0   (also :2.3, :latest)
ghcr.io/dev-art-solutions/inferhub-node:2.3.0
```

Both run as a non-root `app` user. The coordinator listens on `8080` inside the container and
is published on `${INFERHUB_PORT:-5080}` on the host.

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
