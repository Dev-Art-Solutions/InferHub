# v3.36.0 — the provider verification day

**What this is.** Phase 68's whole deliverable (68 D1): the seven releases of the provider track
(v3.29.0 → v3.35.0) each shipped with the same sentence in its notes — *no live provider was called*
— and this is the day that pays for it. Every row says what was run, on what, and what came back.
A claim that could not be checked is a row too, with the reason in it.

**Status: in progress.** Sections 1–5 are the checks that need no vendor credential and they are
done. Sections 6–9 need one real key per vendor and are **not started** — every row in them says so
rather than being absent, so this file cannot be mistaken for a finished day.

## The box, once

| | |
|---|---|
| CPU | AMD Ryzen Threadripper PRO 5975WX (32 cores) |
| GPU | NVIDIA GeForce RTX 3090 Ti — 24 564 MiB, driver 591.86 |
| OS | Windows 11 Pro 26200 |
| Docker | 27.3.1, Linux containers |
| .NET | 10.0.301 |
| Date | 2026-08-27 |

Single observations on one box (68 D1's non-goal: this is not a benchmark).

## 1. The full solution suite, by hand

`dotnet test InferHub.sln`, everything in parallel as CI does.

| Project | Passed | Skipped | Duration |
|---|---|---|---|
| `InferHub.Tests.Shared` | 171 | 0 | 0.2 s |
| `InferHub.Tests.Coordinator` | 720 | 43 | 3 s |
| `InferHub.Tests.Node` | 179 | 3 | 32 s |
| `InferHub.Tests.Mesh` | 403 | 2 | 1 m 1 s |
| **Total** | **1 473** | **48** | exit 0 |

Green as a solution, not only as four slices. The 48 skips are the Postgres/Qdrant gated integration
tests and the `PlanFolderFact` context checks that only run where `plan/` exists.

*(After F1's fix the Coordinator project is 721 — one new test.)*

## 2. The published tags, resolved from the registry (68 D4)

Digests asked of GHCR, not read out of `docker-publish.yml`.

| Tag | Digest |
|---|---|
| `inferhub-coordinator:3.35.0` | `sha256:828b9df97568dbf5ca488e565ac9a29c180757e7b981592d6a5df1566e3ec883` |
| `inferhub-coordinator:latest` | **same** ✓ |
| `inferhub-node:3.35.0` | `sha256:7b339e79942de3441b810902c0c09ac5934d726f031ec4704b36ef2b4fc4fe74` |
| `inferhub-node:latest` | **same** ✓ |

`:latest` is the 343 MB base node. The v3.16.1 tag-race fix is still holding.

**Everything below runs on those two digests**, pulled fresh, not on a from-source build — except
where a row says otherwise.

## 3. The provider-less hub (68 D3's free rows)

Coordinator `3.35.0`, no `Providers:` section at all.

| Claim | Result |
|---|---|
| `/health` | `200` |
| `/api/status` with no credential | **`401`** — phase 60's finding, working as intended |
| `/api/status` from inside the container (`127.0.0.1`) | `200` — the loopback exemption is real; a host request crosses the bridge and is not loopback (21 D5) |
| `coordinatorVersion` | `3.35.0` |
| `providers` key in the payload | **absent entirely** ✓ (61 D2 / 65 D5 — this is what keeps a v3.28-configured hub byte-identical) |
| `metrics.perProvider`, `metrics.providerRefused` | `[]`, `0` |
| `inferhub_provider_refused_total` on the scrape | present at **0** ✓ (66 D6 — a hub with no provider can still refuse a steer) |
| `inferhub_provider_info` | **absent** ✓ (nothing to describe) |

### The console renders — and this is the first time anybody has looked at it

66's release notes said out loud that *every check is payload, scrape and asset bytes* and that the
panel had never been drawn. It has now: headless Edge against `console.html` on the published image,
screenshot at `scratchpad/console-empty.png`.

- **CLOUD PROVIDERS is on the page on an empty hub**, with all eight columns and the sentence
  **"No cloud provider is configured — nothing leaves your machines."** — 66 D1, rendered, in the
  place it was designed to be read.
- Every other panel shows its own empty state; none of them is the reassuring one, which is 66 D1's
  argument visible in one screenshot.
- The header pill reads **offline** while the fleet numbers are drawn and current. Not a defect:
  `streamLoop` sets `offline` when no admin key is set, falls back to polling, and the poll is what
  filled the page. Recorded because it *looks* like one at first glance.

**Method note:** a browser on the host is not a loopback client to a container (21 D5), and the
console holds its admin key in memory only — never `localStorage`, deliberately — so a headless
screenshot cannot type one. The page was reached through a socat sidecar sharing the container's
network namespace, which makes the connection genuinely originate at `127.0.0.1` **inside** the
container. The auth path is what that changes; the rendering is the published image's own bytes.

## 4. Configuration behaviour on the published images (no credential needed)

Hub configured with two providers: `claude` (`anthropic`, key set) and `gem` (`gemini`, **no key**).

| Claim | Result |
|---|---|
| A keyless provider is a needs-attention row, **not** a startup refusal (66 D4) | Hub started, `healthy` ✓ |
| `credential` in the payload and the scrape | `configured` / `absent` — never a key, never a prefix ✓ |
| An unmapped model (`llama3.2`) | `404 model 'llama3.2' not found` — nothing left the hub ✓ (track D4, the map is the consent) |
| A steer at an unknown provider id | `400`, one sentence |
| A steer at a **real** provider that does not claim the model | `400`, **byte-identical sentence** ✓ — a client with a key cannot enumerate the operator's vendors by probing (65 D4) |
| `X-InferHub-Provider: node` | `404` — the prompt was kept off the vendor without an operator editing config ✓ |
| `inferhub_provider_refused_total` after the two steers | `2` — and the `node` steer is **not** counted as a refusal ✓ |
| `/api/tags` | both provider-only models listed, `digest`/`size` **null** ✓ (65 D5) |

### The node's two startup refusals, on the published node image (67 D3, D5)

Both fire, with the vendor's own sentence rather than a stack trace as the headline:

- `Backend:Type=anthropic` with no allowlist →
  *"Upstream:Models:Include (or Node:Models:Include) must name at least one model when
  Backend:Type=anthropic. A cloud vendor's catalogue is tens or hundreds of models this node cannot
  serve, and reporting all of them tells the coordinator to route anything here."*
- `OpenAi:BaseUrl` and `Upstream:BaseUrl` both set and disagreeing →
  *"…Keep one — the pre-v3.35 section still binds, and which upstream receives a prompt is not
  decided by which of two sections was written last."*

*Observation, not a finding:* the conflict is thrown from `UpstreamBackend`'s constructor rather
than from an options-validation pass at boot. It does fail startup, so 67 D3's claim holds as
written.

## 5. F1 — two providers made `/metrics` unparseable, and it shipped in five releases

**The day's first real finding, and the only one so far.**

`PrometheusFormatter.Info` writes its own `# HELP` / `# TYPE` header and was being called **inside a
loop over the configured providers**. With two providers the scrape carries two `# HELP
inferhub_provider_info` lines — and a repeated header for one metric name does not drop that series,
it makes Prometheus **reject the entire scrape**. Every InferHub series disappears from the dashboard
the moment an operator configures a second vendor, which is the thing this whole track exists to
make possible.

Observed on the published `3.35.0` image:

```
# HELP inferhub_provider_info A cloud provider this hub is configured to use.
# TYPE inferhub_provider_info gauge
inferhub_provider_info{provider="claude",…} 1
# HELP inferhub_provider_info A cloud provider this hub is configured to use.   <- second header
# TYPE inferhub_provider_info gauge
inferhub_provider_info{provider="gem",…} 1
```

**Two families were affected**, and the older one is worse:

| Family | Since | Reached when |
|---|---|---|
| `inferhub_provider_last_model` | **v3.29.0** (61) | two providers have each dispatched at least once |
| `inferhub_provider_info` | v3.34.0 (66) | two providers are configured at all |

**Why seven releases of tests missed it.** Every provider test in the suite declares **one**
provider, and the in-test exposition reader did `help[name] = …` — silently overwriting on a
duplicate, which is the one thing a real Prometheus will not do. The bug needs the feature to
actually be used to appear.

**Fixed here** (68 D5 — a shipped feature that does not work is fixed in this release):

- `PrometheusFormatter` — one `Header` per family, `Sample` per row, matching the pattern the
  `inferhub_node_vram_*` families have used since phase 48.
- `Exposition.Parse` in `PrometheusMetricsTests` now **fails on a second HELP or TYPE line for a
  name**, exactly as Prometheus does. That is the durable half: it guards every family, not the two
  that were broken. Running it against the current formatter found no others.
- `TwoProvidersShareOneHeaderPerFamilyRatherThanRepeatingIt` — the regression test, with two
  providers configured and two dispatched.

Verified from source with the two-provider config: **one** header, two samples.

**Shipped as `v3.35.1`, not inside this release.** 68 D5 said a wire-format defect ships fixed in
the verification release; the maintainer's call on the day was that a `/metrics` endpoint Prometheus
rejects should not wait on four vendor keys that are not available yet, and a patch tag says what it
fixed more clearly than a verification-day release does. The day itself stays open at `3.36.0`. See
`.claude/release-notes-v3.35.1.md`.

## 6–9. The vendor sections — NOT STARTED

These need one real key each and none has been used yet. Listed as rows rather than omitted, so the
absence is legible:

| Section | What it will carry | Status |
|---|---|---|
| 6. OpenAI | chat, streamed chat, embeddings, ledger tokens vs the vendor's own, `X-InferHub-Served-By` | **not run — no key** |
| 7. OpenRouter | the same, plus whether the default base URL is *reachable* rather than merely bound (v3.30's own words) | **not run — no key** |
| 8. Anthropic | the same, minus embeddings — an embed must come back `503` naming the capability (67 D4); plus `max_tokens` truncation reading back as `done_reason: length` | **not run — no key** |
| 9. Gemini | the same, plus `ThinkingBudget=0` against a model that honours it, and `:embedContent` dimensions | **not run — no key** |
| Per vendor | a bad key (401), a model the vendor rejects, and what 66 D3's panel shows an operator in the vendor's own words | **not run — no key** |
| Per vendor | the day's spend, and the vendor's usage console beside the hub's ledger (68 D1's load-bearing row) | **not run — no key** |
| The node (68 D6) | a vendor-typed node, meshed and solo, against a real vendor | **not run — no key** |

**Until those are filled in, the track's central claim — that the native dialect is where the token
counts, the stop reasons and the error bodies are honest — remains a claim.** What sections 1–5
establish is everything around it: the consent, the refusals, the console, the configuration
surface, and one shipped defect that a scrape found and no unit test could.
