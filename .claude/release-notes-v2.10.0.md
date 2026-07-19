# InferHub v2.10.0 — Observability export: Prometheus `/metrics`

Everything the mesh measured lived in-memory and was reachable only as `/api/status` JSON and
the status page. That is a snapshot: no Grafana, no alerting, no capacity history, no way to
answer "was the fleet saturated at 3am?" the morning after. `GET /metrics` fixes that.

**This phase exposes numbers; it does not measure any.** Every series here already existed in
`Metrics`, `ThroughputTracker`, `RequestQueue` and `AdmissionControl`. Nothing was added to the
request path.

## What changed

- **`GET /metrics`** returns the Prometheus text exposition format
  (`text/plain; version=0.0.4`). `PrometheusFormatter` is a pure function from a gathered
  `PrometheusScrape` to a string — no services, no clock, no I/O — so a test can assert the
  exact bytes.
- **What is exposed**, all namespaced `inferhub_*`: fleet counters (requests total / in-flight /
  completed / failed, failovers, evictions, OpenAI-surface requests, uptime, `build_info`),
  cloud burst (`fallback_dispatched_total` and an info-style `fallback_last_model{model}`),
  per-node (`node=`) up/cordoned/models/local-in-flight/heartbeat-age plus the routed request
  counters and `node_tokens_per_second{node,model}`, vector and per-collection (`collection=`)
  queries, latency, documents, chunks and failures, the request queue's depth and outcomes and
  median wait, and per-client (`client=`) live windows against their configured limits.
- **`ThroughputTracker.Snapshot()`** — the EWMA map had no way to be enumerated; now it has a
  read-only, ordered one.
- **`deploy/docker/compose.observability.yml`** — an optional Prometheus + Grafana overlay with
  a provisioned datasource and a starter **InferHub mesh** dashboard. It stays an overlay: the
  base stack is still two services, and nobody who just wants a mesh has to run a monitoring
  stack to get one.

## Auth — recorded, because it was a real decision

`/metrics` is operational, like `/health`, which is deliberately open. But unlike `/health` it
exposes node names, model names, client ids and the shape of your traffic. So it is
**admin-key-guarded by default** — `AdminApiKeyMiddleware` now guards a small prefix *set*
rather than one constant — and `Metrics:OpenScrape=true` drops the guard on **this one
endpoint** for a trusted network. It never opens `/api/admin/*`, and there is a test that says
so.

It is deliberately **not** under the bearer inference guard. A scraper is not a client, and
handing a monitoring system a token that can spend GPU time would be the wrong trade.

## Counts, never content

The per-client series come from the in-memory admission windows — the same source
`/api/admin/clients` already reads — and never from the usage ledger. The ledger is append-only
history and is never read to drive anything; that is the reasoning that keeps it from being a
second source of truth (rule 4 / phase-25 D2), and a metrics endpoint reading it would have
quietly ended that.

## Absence is a fact, so absence is what you get

An unmeasured `(node, model)` produces **no** `node_tokens_per_second` series rather than a
zero. The router treats an unmeasured node as *average*, never as slow (phase 26, D4) — a 0 on
a dashboard would be a lie that invites an alert against a node that has simply not been asked
yet. Same for a client limit that is unset (unlimited is the absence of a series, not a 0 and
not a `-1` sentinel a dashboard would happily plot) and for the queue's median before anything
has ever queued. The fleet counters, by contrast, are always present at 0 — there, a zero is a
statement rather than an absence.

## Not a migration

`/api/status` and the status page are **unchanged**. This adds a surface; it does not replace
one.

## Tests

539 total (522 passed, 17 skipped — the gated Postgres integration tests, as always). New:
`PrometheusMetricsTests` parses the output back with a minimal in-test exposition parser rather
than string-matching it — substring assertions would pass happily on output no Prometheus could
read, which is the exact failure this endpoint exists to avoid. It covers `# HELP`/`# TYPE` on
every series, counter-vs-gauge typing, values matching what `Metrics` recorded, label
cardinality (a node id is a label, never baked into a metric name), label-value escaping,
invariant decimal separators on **every** value line (a decimal comma would be a locale bug
that only appears on a Bulgarian or German host and would sink the whole scrape), the
absent-not-zero cases, and the four auth cases including `OpenScrape=true` not unlocking
`/api/admin`.

## Also fixed: the node image has been dead at startup since v2.3.0

Not a phase-28 change, and not a regression — a **shipped bug found by running the container**,
which is the only way this class is ever found (D7). Bringing the documented compose stack up to
verify the Grafana overlay, the node died before it ever reached the coordinator:

```
System.UnauthorizedAccessException: Access to the path '/app/.inferhub-node-id' is denied.
```

`FileNodeIdentity` persists the stable node id to `Node:DataDirectory`, which defaults to the
content root — `/app`, which `USER app` cannot write. This is the **same root cause** as the
v2.5.1 fix, whose two lines covered only the *vector replica* half. That half was conditional on
a feature being enabled, so it looked like the whole bug; the identity write is unconditional, so
the node image has in fact been broken on every run since v2.3.0.

Two lines, matching the existing pattern:

- `src/InferHub.Node/Dockerfile` — `ENV Node__DataDirectory=/data` (the already-chowned dir).
- `deploy/docker/docker-compose.yml` — the node's volume moves from `node_replicas:/data/vector-replicas`
  to **`node_data:/data`**, so the node id and the replicas share the one mount point the image
  creates and owns. A volume at a path the image does not contain is created root-owned, which is
  the same trap a second time.

**Upgrade note.** The volume rename means an existing stack drops its old `node_replicas`
volume; the coordinator re-pushes the replicas, and the node takes a new id on first start. Both
are correct and both are one-time. `docker volume rm inferhub_node_replicas` to reclaim the space.

## Verified live

A running coordinator from source: `curl localhost:5080/metrics` returns valid exposition text
with the correct content type and `build_info` carrying the real informational version.

Then the full documented stack, built and run: coordinator + node + Prometheus + Grafana. The
Prometheus target reports `health: up`, scraping `/metrics` **with the admin key over the bridge
network** — so the non-loopback auth path is proved, not assumed. After one real
`/api/chat` against a `qwen2.5:0.5b` node:

```
inferhub_build_info{version="2.10.0"} 1
inferhub_node_up{node="58e35f0d-…",name="docker-node"} 1
inferhub_node_tokens_per_second{node="58e35f0d-…",model="qwen2.5:0.5b"} 166.94889
inferhub_node_requests_completed_total{node="58e35f0d-…"} 1
```

Grafana provisioned the **InferHub mesh** dashboard and its panels resolve real data through the
datasource. The datasource `uid` is now **pinned** in the provisioning file (`inferhub-prometheus`)
and referenced by the dashboard JSON — left unset, Grafana generates a different uid per install
and every panel of a provisioned dashboard comes up "datasource not found".

## Zero new dependencies

Rule 5 holds. The exposition format is `# HELP` / `# TYPE` / `name{labels} value` — the same
"three lines of string formatting" reasoning that kept the NDJSON framing (phase 9) and the SSE
framing (phase 21) dependency-free. `prometheus-net` would have been a permanent dependency and
a registry abstraction on the hot path in exchange for code that fits on a screen. An OTLP
*push* exporter would genuinely need a package; it stays deferred and opt-in, if demand appears.
