# InferHub v3.46.0 — an OTLP push exporter, for an operator with no Prometheus server

`/metrics` (phase 28) has always been pull-only. That is the right default for a Prometheus
deployment and no help at all to an operator whose observability stack is Grafana Cloud,
Honeycomb, Datadog, or the reference `otelcol` — every one of which speaks OTLP. This release adds
a push exporter that sends the same numbers `/metrics` already serves to any OTLP-compatible
collector on an interval, opt-in and off by default.

## What's new

```json
"Observability": {
  "Otlp": {
    "Enabled": true,
    "Endpoint": "http://otel-collector:4318",
    "IntervalSeconds": 30,
    "Headers": { "x-honeycomb-team": "your-ingest-key" }
  }
}
```

With `Enabled=true`, a background loop reads `/metrics`'s own exposition text back out and `POST`s
it as an OTLP/HTTP JSON payload to `{Endpoint}/v1/metrics` every `IntervalSeconds`. `Enabled=false`
(the default) is byte-for-byte v3.45.1: zero new HTTP calls, zero behaviour change.

**Zero new dependencies.** The OpenTelemetry .NET SDK was considered and declined — its exporter
package and transitive protobuf/gRPC pieces are exactly what rule 5 exists to keep out, to
serialize numbers this codebase already computes in one place. OTLP's JSON encoding is a
documented, stable wire format every collector accepts on the same port as the binary one, so the
exporter is a hand-rolled `HttpClient` — the same call this codebase already made for the Qdrant
connector's REST API instead of its gRPC client.

`ExpositionReader` (new, production code) parses `PrometheusFormatter`'s own output back into rows,
so the OTLP exporter reuses one source of truth instead of re-deriving every metric family a second
time from the underlying registries — the exact bug shape (two places to remember a metric) that
reached a published image once already (phase 68 F1's duplicate-`HELP` finding). `OtlpFormatter`
turns those rows into an OTLP payload: `counter` becomes a monotonic, cumulative `Sum` (with
`startTimeUnixNano` pinned to the coordinator's own start time), `gauge` becomes a plain `Gauge`.
Histogram families (`inferhub_image_job_seconds_*`, the one histogram in the whole scrape) are
skipped — recorded as skipped rather than silently dropped, since a correct OTLP histogram data
point needs a second parser this phase's own ask does not require yet.

A push failure is logged at `Warning` and discarded. There is no retry queue: a stale gauge resent
one interval late is not "restored," it is a value from the past reported as now, and metrics on
this hub are already lossy by design (rule 4 — everything but the vector store resets on restart).

`ScrapeSnapshotBuilder` is new too, and it is not a feature so much as a debt paid: assembling a
`PrometheusScrape` from every registry used to live only inside `MetricsEndpoint`'s handler, fine
when there was exactly one caller. The OTLP exporter is the second, so the assembly moved to the
one place that knows how.

`src/InferHub.Coordinator/CLAUDE.md` was sitting at its 1100-line budget, so phases 28 and 66 moved
out — alongside this phase's own decisions — into a new `src/InferHub.Coordinator/Observability/
CLAUDE.md`, the same move phases 62 and 67 made for the vector and tool-runtime subtrees. Phase 28's
own text used to say an OTLP exporter would need a package; that sentence was corrected rather than
left standing, per the root file's own rule against a caveat a later phase makes false.

## Verified

**Full regression green:** `dotnet test InferHub.sln` — 193 (Shared) + 824 (Coordinator, 43
Postgres-gated skips) + 192 (Node, 3 gated skips) + 441 (Mesh) = 1650 tests, all passing. (One Mesh
test, `ToolUploadTests.TheNodeEnforcesItsOwnCeilingEvenWhenTheHubAcceptedTheUpload`, is a
pre-existing intermittent flake under full-suite socket contention — unrelated to this phase, passes
reliably alone and on repeat full-suite runs, not touched by this release.)

**Against a real OTLP collector, not just tests:** ran the coordinator locally with
`Observability:Otlp:Enabled=true` pointed at a real `otel/opentelemetry-collector-contrib:0.116.1`
container's OTLP/HTTP receiver. Every push answered `200`, the collector's own debug exporter parsed
and logged every metric with no error — 24 metrics / 24 data points per tick, `inferhub_requests_
total` decoded as `Sum`/`IsMonotonic: true`/`AggregationTemporality: Cumulative` with a stable
`StartTimestamp` across ticks, `inferhub_queue_depth` decoded as a plain `Gauge`, and the resource
carried `service.name: inferhub-coordinator` / `service.version: 3.46.0+<commit>`.

**Addendum, same day — re-verified against the published image itself.** After the tag built,
pulled `ghcr.io/dev-art-solutions/inferhub-coordinator:3.46.0` and ran it for real: two containers
on one Docker network, the published coordinator image pushing to a real `otelcol` container over
container-to-container DNS, no host loopback tricks. Same result as the local check — every push
`200`, 24 metrics / 24 data points per tick, no error anywhere in the collector's log, and the
resource this time reading `service.version: 3.46.0` (no `+<commit>` suffix, because the published
image's build strips the informational-version metadata the local `dotnet run` carries — a cosmetic
difference, not a defect). The local-binary check above and this one now both stand.

## What is still not established

The two items phase 80 left open (a full ingest → hybrid-search → reranked-order comparison through
the retrieval pipeline itself, and a run through a real coordinator+node pair) are unrelated to this
phase and remain open from before it.
