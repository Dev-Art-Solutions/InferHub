# v3.34.0 — Five releases of provider work, on a page: a panel, a failure counter, and the vendor's own words

Phase 66, the sixth release in the v3.29–v3.36 provider track and the one that makes the previous
five operable. Since v3.29 a hub can name four vendors, give each its own credential and reach each
through its own dialect; since v3.33 a provider can be asked *first*. **None of it was on the
console.** The whole feature was legible from one admin-gated JSON payload, and from two metric
series that appear only *after* traffic has already left — so "is my hub configured to send prompts
to a vendor" and "has it" were the same silence.

The failure path was worse. A wrong key, a typo'd base URL or a vendor having a bad afternoon
produced a `LogWarning` on the hub and a 502 in front of a user. Nothing counted it, nothing kept the
sentence the vendor said, and a `prefer` provider that was failing looked exactly like a hub quietly
answering from its own fleet.

**A deployment that changes no config behaves identically to v3.33.** No `providers` key on
`/api/status`, no per-provider series, and the console says so in words. The single addition to such
a hub's scrape is `inferhub_provider_refused_total 0`.

## The Cloud providers panel

One row per place a prompt can go: the policy, whether a credential is set, the models that provider
claims, dispatches, failures, and **the upstream's own sentence about the most recent failure** —
`Incorrect API key provided.` rather than a 502 and an afternoon.

**It is the one panel on the console that stays visible when it is empty.** A hub with no provider
renders *No cloud provider is configured — nothing leaves your machines.* Every other panel hides;
this one must not, because that sentence is the feature. A panel that vanishes when the answer is the
reassuring one teaches an operator to read absence as "I could not tell", which is the one state
cloud burst may never be in.

A deployment still on the pre-v3.29 `Fallback:` section gets a row too, marked `legacy` — **drawn
from the `fallback` block, not from `providers[]`**. The payload still does not list the projected
legacy upstream as a provider, because that is what keeps a v3.28-configured hub byte-identical, and
`TheLegacyUpstreamIsNotAProviderInThePayloadEvenThoughTheConsoleDrawsItAsARow` is the test that stops
somebody tidying the projection into the array the console reads. Its `credential` cell is a dash on
purpose: the legacy block gains no new key for a panel, since a new field there would land in the
payload of every deployment that changed nothing.

## Needs attention grew three provider rows, and a missing key is one of them

A provider that is enabled, maps models and has **no credential** is not a startup failure and must
not become one: an OpenAI-compatible endpoint on your own network legitimately has no key, and
refusing to boot without one would break a supported deployment to catch a typo. Against a vendor it
is the purest form of "I turned it on and nothing happened" — a 401 on the first prompt, hours later,
in front of a user. So it is a row in the strip, alongside a provider whose last dispatch failed and
a failing `only` provider, which is named separately because its models have no fleet backstop by
construction.

The strip's second column is **Where** rather than **Node** from this release: half its rows now name
a vendor rather than a box.

## The series, and why one of them may exist where a zero counter may not

| Series | Labels | Present |
|---|---|---|
| `inferhub_provider_info` | `provider`, `type`, `policy`, `credential` | per configured provider, **before any traffic** |
| `inferhub_provider_failed_total` | `provider` | once that provider has failed something |
| `inferhub_provider_refused_total` | — | always, at zero |

Phase 28 refuses a zero *counter* for a feature nobody switched on, because it reads as measured
traffic. An info series with a constant 1 measures nothing — it describes configuration, the shape
`inferhub_profile_state` and `inferhub_image_recipe` already have — and it is what makes the absence
of `inferhub_provider_dispatched_total` readable: without it, *no vendor configured* and *a vendor
configured that has served nothing* are indistinguishable, and those are opposite answers.

No API key, no base URL and no model name is in any of those labels: the first is rule 7, the second
carries a token in a query string often enough that it cannot be volunteered to a scrape, and the
third is unbounded cardinality for a fact `/api/status` already carries.

**`inferhub_provider_refused_total` deliberately has no label at all.** The id a caller steers at is
text they chose, so labelling by it would let anyone holding an inference key mint unbounded series
by sending a header; labelling by the provider that *does* claim the model would rebuild by scrape
the vendor enumeration v3.33 refused to expose by probing. And `inferhub_requests_failed_total` is
**not** incremented by a provider failure — a `prefer` provider that fails is usually followed by a
node answering successfully, and one request must not be able to fail twice in one number.

## Rule 7, argued rather than assumed

The vendor's error text is the reason to open the panel, and it is also the one thing here that could
carry content: an error message is a sentence *about* a request, but nothing stops a vendor quoting a
prompt inside one. So it is treated as content — held **once per provider**, in memory, never
persisted, never a metric label, and reachable only through the admin-gated `/api/status`. Same
footing as the tool runtime's `lastError`, which this row is modelled on. A ring of recent errors was
rejected: that is a log, and this hub's answer to logs is that it does not keep them.

## One thing paid off from two releases ago

`GeminiUpstreamClient.ErrorFrame` deserialized **every** streamed frame into an error envelope to
discover it was an ordinary delta. The OpenAI dialect has had a `"error"` substring guard in front of
that since v3.30; v3.32 recorded the missing one as a cost rather than a defect, v3.33 did not take
it either, and it is here. The check is unweakened — a frame carrying the envelope still contains the
key and still raises — and `AFrameWhoseTextMentionsAnErrorIsStillAnAnswer` is the guard on the guard.

## What was not established

- **That a real vendor's error text is what an operator ends up reading in the panel.** Every payload
  in this repository's provider tests is recorded, by design: a test that needs somebody's API key is
  a test CI cannot run and a card billed on every commit. The failures asserted here are a stub
  returning OpenAI's own 401 envelope and a refused TCP connection. Phase 68 is the day with one real
  key per provider.
- **That the panel renders correctly in a browser.** The read-set contract test proves every field
  the console reads exists in the payload and that no field it reads has drifted; it does not open a
  browser. The from-source run below drove the payload and the scrape, not the DOM.
- **Nothing was measured about the Gemini frame guard.** It removes a JSON parse per streamed frame;
  no benchmark was run, and none is claimed.

Zero new `PackageReference`s. `InferHub.Shared.csproj` is still an empty `<Project Sdk="…">`.

## Driven from source before tagging

A hub with one keyless `openai-compatible` provider at `Policy: prefer`, mapping `smart` to a base
URL nothing listens on:

1. `/api/status` — `"policy":"prefer"`, `"credential":"absent"`, `"failed":0`, `"lastError":null`.
2. `/metrics` — `inferhub_provider_info{provider="vendor",type="openai-compatible",policy="prefer",credential="absent"} 1`
   and `inferhub_provider_refused_total 0`, with **no** `inferhub_provider_dispatched_total`.
3. One chat for `smart` → the dispatch is attempted and fails. `dispatched` 1, `failed` 1, and
   `lastError` is *"No connection could be made because the target machine actively refused it.
   (127.0.0.1:9)"* — the transport's own sentence, not an invented one.
4. `X-InferHub-Provider: nope` → `400`, the sentence names the pair the caller typed and nobody else,
   and `inferhub_provider_refused_total` is 1.

---

## Addendum — the published image, checked the same evening

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.34.0`, pulled and driven by hand. The image's own
label reports `version=3.34.0`, `revision=6ac1ded` — the phase commit, asked of the artefact rather
than of a dashboard.

A hub with one **keyless** `openai-compatible` provider at `Policy: prefer`, mapping `smart` to a
base URL nothing listens on, and no node at all:

1. **`/api/status` before any traffic** — `"policy":"prefer"`, `"credential":"absent"`,
   `"failed":0`, `"lastError":null`. Reported because it is configured, not because it was used.
2. **`/metrics` before any traffic** — `inferhub_provider_info{provider="vendor",type="openai-compatible",policy="prefer",credential="absent"} 1`
   and `inferhub_provider_refused_total 0`, with **no** `inferhub_provider_dispatched_total` and no
   `inferhub_provider_failed_total`. The description exists; the measurements do not. That pair is
   the whole of D5, on the artefact.
3. **One chat for `smart`** — the dispatch is attempted and fails. `dispatched: 1`, `failed: 1`,
   `lastError: "Connection refused (127.0.0.1:9)"`, and `lastErrorAtUtc` 66 ms after `lastAtUtc`.
   The transport's own sentence, not one this hub composed.
4. **`inferhub_provider_failed_total{provider="vendor"} 1`** appears only now, beside the dispatch
   counter — and the error text is **absent from the whole scrape**.
5. **`X-InferHub-Provider: nope`** — `400`, and the sentence names the pair the caller typed and
   nobody else; the word `vendor` does not appear in it. `inferhub_provider_refused_total` goes to 1
   with no label on it.
6. **No key anywhere in `/api/status`** — neither the admin key nor the client key appears in the
   payload, and the provider has none to leak in the first place.
7. **The console assets on the image carry the panel** — `console.html` has the *Cloud providers*
   section and `console.js` carries *"No cloud provider is configured — nothing leaves your
   machines."*, so the empty state ships rather than being a local edit.
8. **A `Fallback:`-only hub on the same image** — `/api/status` has **no `providers` key at all**,
   the `fallback` block is byte-shaped exactly as it was (`enabled`, `trigger`, `mappedModels`,
   `dispatched`, `lastModel`, `lastAtUtc`), and the scrape carries **no** `inferhub_provider_info`,
   **no** dispatch and **no** failure series — only `inferhub_provider_refused_total 0`. That is the
   invariant this phase was likeliest to break, and the one addition, checked rather than argued.

**What this run did not establish, and could not:** that the panel *renders* correctly. Every check
above is against the payload, the scrape and the asset bytes; nobody opened a browser at it, and no
live vendor was called — the failure driven here is a refused TCP connection rather than a real
vendor's 401. Both are phase 68's day.
