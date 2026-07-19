# InferHub v2.10.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
r/selfhosted and r/LocalLLaMA drafts included; post by hand.

---

## Facebook

> InferHub 2.10 gives the mesh a `/metrics` endpoint.
>
> Prometheus scrapes it, Grafana draws it: fleet in-flight, per-node measured tokens/second,
> queue depth, retrieval latency per collection, per-client token windows. The numbers were
> always there — they were just a snapshot on a status page. Now they have a history, a graph
> and an alert.
>
> A Prometheus + Grafana overlay with a starter dashboard ships in `deploy/docker/`, as an
> overlay: the base stack is still two services.
>
> We wrote the exposition format by hand, so it's still zero new dependencies. And an
> unmeasured node gets *no* series rather than a zero — a 0 on a dashboard is a lie that
> invites an alert against a node nobody has asked anything yet.
>
> Self-hosted, MIT, .NET 10.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub 2.10: `/metrics` for Prometheus, plus a Grafana overlay. Per-node tokens/sec, queue
> depth, retrieval latency — now with history and alerts. Exposition format hand-written, zero
> new deps. MIT, .NET 10.
> https://github.com/Dev-Art-Solutions/InferHub

_(≈235 chars incl. the t.co-counted link — under 280.)_

## r/selfhosted / r/LocalLLaMA (draft)

**Title:** InferHub 2.10: a Prometheus /metrics endpoint (and a Grafana dashboard) for a self-hosted GPU mesh

Body: InferHub is a self-hosted, Ollama/OpenAI-compatible inference mesh in .NET — a coordinator
on an always-on box, nodes on your GPU machines that dial out (no port forwarding). Up to now
everything it measured was in-memory and readable only as `/api/status` JSON, which is a snapshot:
no alerting, no capacity history. 2.10 adds `GET /metrics` in the Prometheus text exposition
format — fleet in-flight, per-node measured tokens/second (the EWMA the router already uses to
route), queue depth and outcomes, per-collection retrieval latency, per-client token windows
against their configured limits. `deploy/docker/` gets an optional Prometheus + Grafana overlay
with a provisioned starter dashboard.

Two decisions worth calling out. The endpoint is **admin-key-guarded by default** — it's
operational like `/health`, but unlike `/health` it leaks node names, model names and traffic
shape, so you opt into an open scrape (`Metrics:OpenScrape=true`) rather than out of it; a
scraper is not an inference client and shouldn't hold a token that can spend GPU time. And
**absence is meaningful**: an unmeasured (node, model) has no throughput series at all rather
than a 0, because the router treats an unmeasured node as *average*, and a zero on a graph would
be a lie that pages someone.

The exposition format is hand-written — it's `# HELP` / `# TYPE` / `name{labels} value`, the same
reasoning that kept the NDJSON and SSE framing dependency-free. MIT, zero new deps, `/api/status`
unchanged.
