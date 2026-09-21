# InferHub.Coordinator/Observability — agent context

**Scope: `src/InferHub.Coordinator/Observability/`.** The Prometheus scrape (`/metrics`), the
`Metrics` registry every other subsystem writes into, and the OTLP push exporter (phase 81). **Split
out of the root `src/InferHub.Coordinator/CLAUDE.md` in phase 81**, the same reason phases 31/35/44
moved to `src/InferHub.Coordinator/Vector/CLAUDE.md` (62 D6) and 41/42/48/55–58 to
`src/InferHub.Node/Tools/CLAUDE.md` (67 D6): the root file was at its 1100-line budget and this was
the largest coherent subtree the provider track had nothing to do with.

> **Read the root `CLAUDE.md` first** — the seven design rules bind everything here, especially
> **rule 5** (no new heavy dependencies) and **rule 7** (no request content on the coordinator,
> which includes what a metric label may carry).

## Related context

- Everything else on this host: `src/InferHub.Coordinator/CLAUDE.md`
- What `Metrics` counts and why each counter is shaped the way it is:
  [Metrics.cs](src/InferHub.Coordinator/Observability/Metrics.cs) — the type itself is the
  documentation; every field has a comment naming the phase that added it.

## Decisions recorded here

### Phase 28 (Prometheus `/metrics`) — also load-bearing

**D1 — The exposition format is hand-written, and `prometheus-net` stays out.**
[PrometheusFormatter](src/InferHub.Coordinator/Observability/PrometheusFormatter.cs) is a pure
function from a gathered `PrometheusScrape` to a string. The format is `# HELP` / `# TYPE` /
`name{labels} value` — the same "three lines of string formatting" reasoning that kept the NDJSON
(phase 9) and SSE (phase 21) framing dependency-free. Rule 5 survived again: **zero new
dependencies**. *(Phase 81 settled the question this paragraph used to leave open: an OTLP push
exporter did not need a package either — see phase 81 D1 below. This sentence used to say the
opposite; the root `CLAUDE.md` rule about a caveat a later phase makes false is why it was rewritten
rather than left standing.)*

**D2 — This phase exposes numbers; it measures none.** Every series comes from `Metrics`,
`ThroughputTracker`, `RequestQueue` and `AdmissionControl`, all of which already computed it.
Nothing was added to the request path, and `/api/status` is **unchanged** — this adds a surface,
it does not migrate one. If a future change starts *measuring* in the formatter, it has drifted.

**D3 — `/metrics` is admin-guarded by default, and is not under the bearer guard.** It is
operational like `/health` (which is open), but unlike `/health` it exposes node names, model
names, client ids and traffic shape. So `AdminApiKeyMiddleware` now guards a small **prefix set**
(`/api/admin`, plus `/metrics` unless `Metrics:OpenScrape`) rather than one constant.
`OpenScrape=true` opens **only** the scrape endpoint — `PrometheusMetricsTests` fails if it ever
unlocks `/api/admin/*`, which would be a config flag that quietly grants cordon and model-pull to
anyone who can reach the port. It is deliberately not under `BearerApiKeyMiddleware`: a scraper is
not an inference client and must not hold a token that can spend GPU time.

**D4 — Client series come from `AdmissionControl`, never from the usage ledger.** The ledger is
append-only history and is never *read* to drive anything (rule 4 / phase-25 D2) — a metrics
endpoint reading it would have quietly ended that reasoning. Counts only; there is no content
anywhere in the usage path (rule 7).

**D5 — Absence is a fact, so absence is what is emitted.** An unmeasured `(node, model)` has **no**
`inferhub_node_tokens_per_second` series rather than a `0`: the router treats an unmeasured node as
*average*, never as slow (phase 26, D4), and a zero on a dashboard is a lie that pages someone about
a node nobody has asked anything yet. Same for an unset client limit (unlimited is no series — not
`0`, and not a `-1` sentinel a dashboard would happily plot) and for the queue's median before
anything has queued. The **fleet** counters are the opposite and always present at zero, where a
zero is a statement rather than an absence.

> `PrometheusMetricsTests` **parses the output back** with a minimal in-test exposition reader
> rather than string-matching it. Substring assertions pass happily on output no Prometheus can
> read, which is the exact failure this endpoint exists to avoid. It also asserts an invariant
> decimal separator on every value line — a decimal comma is a locale bug that only appears on a
> Bulgarian or German host and sinks the whole scrape.

### Phase 66 (console, metrics and docs for the provider track) — load-bearing

**D1 — The Cloud providers panel is fed by `/api/status` alone, and it is the one panel that stays on
the page when it is empty.** Every other panel hides; this one renders *No cloud provider is
configured — nothing leaves your machines.* That sentence is the feature (22 D5's question, answered
where somebody is already looking), and a panel that vanishes when the answer is the reassuring one
teaches an operator to read absence as "I could not tell". **Rejected: a new admin route** — the data
is in the poll already, and a second surface is a second thing to keep in step.

**D2 — The console draws the projected `Fallback:` upstream as a row; the payload still does not
carry it.** 61 D2 and 65 D5 keep it out of `providers[]` so a v3.28-configured hub is byte-identical,
and that is unchanged — `console.js` synthesizes the row from the `fallback` block it already reads
and marks it `legacy`. Its `credential` cell is a dash on purpose: **the legacy block gains no key
for this panel**, because a new field there would land in the payload of every deployment that
changed nothing. `TheLegacyUpstreamIsNotAProviderInThePayloadEvenThoughTheConsoleDrawsItAsARow` is
the guard against somebody tidying the projection into the array.

**D3 — A failed dispatch is counted per provider and the vendor's own sentence is kept — one, in
memory, admin-gated.** `inferhub_provider_failed_total{provider}` plus `failed` / `lastError` /
`lastErrorAtUtc` on the status block. **`inferhub_requests_failed_total` is deliberately not
incremented**: a `prefer` provider that fails is usually followed by a node answering successfully,
and one request must not fail twice in one number. **Rule 7, argued rather than assumed:** an error
message is a vendor's sentence *about* a request, but nothing stops a vendor quoting a prompt inside
one — so it is treated as content, held once per provider, never persisted, never a metric label, and
reachable only through the admin-gated payload. **Rejected: a ring of recent errors**, which is a log.

**D4 — A provider with no credential is a needs-attention row, not a startup refusal.** The validator
has never demanded an `ApiKey` and must not: an `openai-compatible` endpoint on your own network
legitimately has none. Enabled, mapping models and keyless against a vendor is the purest "I turned
it on and nothing happened" (45 D1), so the strip carries it, alongside a failing provider and — named
separately — a failing `only` one, whose models have no backstop by construction. The strip's second
column is **Where** rather than Node since this phase, because half its rows now name a vendor.

**D5/D6 — `inferhub_provider_info` describes; `inferhub_provider_refused_total` counts and carries no
label.** An info series with a constant 1 measures nothing, so 28 D5 does not reach it — and it is
what makes the absence of `inferhub_provider_dispatched_total` legible, since without it *no vendor
configured* and *a vendor that has served nothing* are the same silence. No key, no base URL (they
carry tokens in query strings in the wild) and no model names (cardinality) in its labels. The
refusal counter has **no label at all**: the id a caller steers at is text they chose, so labelling it
lets anyone with an inference key mint unbounded series, and labelling it with the provider that
*does* claim the model rebuilds by scrape the enumeration 65 D4 refused to expose by probing. It is
emitted at zero like the other hub-wide counters — a hub with no provider can still refuse a steer.

> **A `# HELP` belongs to the metric *family*, not to the row — and the second one rejects the whole
> scrape.** `Info` writes its own header, so calling it in a loop emitted a duplicate header for
> `inferhub_provider_info` (since v3.34.0) and `inferhub_provider_last_model` (since **v3.29.0**);
> Prometheus refuses the entire endpoint, so every InferHub series left the dashboard the moment an
> operator configured a **second** provider — the configuration this whole track exists to make
> possible. Fixed in **v3.35.1**: one `Header` per family, `Sample` per row, as the
> `inferhub_node_vram_*` families have done since 48. Every provider test declared one provider, and
> the in-test reader overwrote a duplicate silently; **`Exposition.Parse` now fails on a repeated
> header for any name**, which is the half that guards the families nobody has written yet. Found by
> scraping a published image with two providers on it (phase 68), not by a suite.

### Phase 81 (an OTLP push exporter) — load-bearing

**D1 — OTLP/HTTP JSON over a hand-rolled `HttpClient`, not the OpenTelemetry .NET SDK.** The SDK's
package tree (the exporter, its transitive protobuf/gRPC pieces) is exactly what rule 5 exists to
keep out, to serialize numbers this codebase already has in one place. OTLP's JSON mapping is a
documented, stable encoding every collector accepts on the same port as the binary one — the same
reasoning that kept the Qdrant connector on hand-rolled HTTP instead of its gRPC client ("the house
already speaks HTTP-to-a-server by hand"). **Considered and rejected: OTLP/HTTP protobuf**, which
needs a codegen step or a runtime library for no benefit a JSON body about a few hundred numbers
does not already have. Zero new `PackageReference`; `InferHub.Shared.csproj` is untouched.

**D2 — [ExpositionReader](src/InferHub.Coordinator/Observability/ExpositionReader.cs) reads
`PrometheusFormatter`'s own output back out, rather than a second walk over every registry.**
`PrometheusFormatter.Format` already knows every label shape across nine phases, including the
phase-68 duplicate-`HELP` bug (66's own note above). A second formatter built straight from the
registries makes every future metric addition two places to remember — the exact bug shape that
reached a published image once already, just moved. One string is serialized twice instead of one
set of facts computed twice. It is a plain-code cousin of `PrometheusMetricsTests.Exposition`, which
parses this identical grammar for the test suite; this one throws on a malformed line instead of
asserting, because its input is always this coordinator's own formatter output, never operator text.

**D3 — Histogram families are skipped; only `counter` and `gauge` cross to OTLP.**
`inferhub_image_job_seconds_bucket/_sum/_count` (phase 51 D2) is the one histogram in the whole
scrape, as pre-computed cumulative buckets. Recovering a correct OTLP histogram data point from that
— non-cumulative per-bucket counts, matching bounds, the synthetic `+Inf` row — is a second parser
for one family nothing in this phase's ask needs yet. `OtlpFormatter.Build` returns the skipped
family names rather than silently dropping them, so a test can assert the omission is deliberate.

**D4 — A cumulative sum's `startTimeUnixNano` is `Metrics.StartedAtUtc`, already exactly what a
Prometheus counter's "since process start" means.** Every `counter`-typed family becomes an OTLP
`Sum` with `AGGREGATION_TEMPORALITY_CUMULATIVE` and that start time; every `gauge`-typed family
becomes a plain `Gauge` with none — matching what the `# TYPE` line already said.

**D5 — Gated the way `AutoScalerService`/`CorpusFailoverService` already are: `Observability:Otlp:
Enabled` read from `IConfiguration` inside `ExecuteAsync`, default `false`, logged and returned early
when off.** `OtlpExporterOptions` still binds the *shape* (endpoint, interval, headers) at startup —
the same split `MetricsOptions` draws between "is this reachable" and "what does this do".
**Rejected: `IOptions<T>` for the enable flag itself** — every gated `BackgroundService` here reads
its own switch from `IConfiguration` at tick time, and a fourth pattern for the same decision buys
nothing.

**D6 — A push failure logs once at `Warning` and is discarded; there is no retry queue.** Metrics
here are already lossy by design (rule 4: everything but the vector store resets on restart), and a
collector being briefly unreachable is not a fact this hub should hold state about. **Considered and
rejected: a small in-memory retry buffer** — a `Gauge` re-sent one interval late is not "restored,"
it is a value from the past reported as now.

**D7 — Optional static headers (`Observability:Otlp:Headers`) are forwarded verbatim; the hub invents
no credential.** Some collectors (Honeycomb, Grafana Cloud) gate ingest on a header-carried key. No
OTLP resource attribute or metric carries anything `/metrics` does not already expose — rule 7 is
unchanged, this transports the same numbers over a second wire.

> [ScrapeSnapshotBuilder](src/InferHub.Coordinator/Observability/ScrapeSnapshotBuilder.cs) is new in
> this phase too, and it is not a decision so much as a debt paid: assembling a `PrometheusScrape`
> from every registry used to live only inside `MetricsEndpoint`'s handler, which was fine with one
> caller. `OtlpMetricsExporterService` is the second, so the assembly moved to the one place that
> knows how, the same argument D2 makes for the formatter it reads.
