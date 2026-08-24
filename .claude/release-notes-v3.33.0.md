# v3.33.0 — A provider becomes a routing target: a policy per model, a steer per request, one discovery surface

Phase 65, the fifth release in the v3.29–v3.36 provider track and the one the previous four were
building towards. Since v3.29 you can name four vendors, give each its own credential and reach each
through its own dialect — and **none of them was ever a routing target.** `InferenceCore` asked the
router first and the provider second, so an upstream was the thing that happened after the fleet
failed. "Serve `smart` from Anthropic while my own boxes stay busy with local models" was not a
sentence the configuration could say.

```jsonc
"Providers": {
  "claude": {
    "Type": "anthropic",
    "Policy": "prefer",                       // asked first; the fleet is the backstop
    "ModelPolicy": { "cheap": "no-node" },    // …except for this one
    "ModelMap": { "smart": "claude-opus-5", "cheap": "claude-haiku-4-5" }
  }
}
```

```bash
curl -H 'X-InferHub-Provider: claude' ...   # this one, or 400
curl -H 'X-InferHub-Provider: node'   ...   # nobody's vendor sees this one
```

**A deployment that changes no config behaves identically to v3.32** — the `404`, the
`X-InferHub-Served-By` values, the `/api/tags` listing and the `/api/status` payload of a hub with
only the legacy `Fallback:` section are all byte-for-byte what they were.

## `Policy` is `Trigger` with two more values, and that is deliberate

| `Policy` | When the provider is asked |
|---|---|
| `no-node` | Default, and what every release since v2.4 did. |
| `no-node-or-saturated` | Also when every node holding the model is at its declared cap. |
| `prefer` | **First.** The fleet is the backstop if the call fails. |
| `only` | **Always**, and a node holding that name never serves it. |

`Trigger:` keeps binding and keeps meaning what it meant. What was rejected is instructive: a
`Preferred: true` boolean beside `Trigger` is four combinations of two knobs, two of them nonsense;
adding the new values *under the name* `Trigger` leaves a field called "trigger" whose value is
"always, first", which is a lie in a config file and a support question every release.

**Both set and disagreeing fails startup naming both.** Precedence is not how this hub decides whose
servers see a prompt — the same argument v3.29 made for refusing a model claimed by two providers.
Making that check exact needed one small change: `Providers:<id>:Trigger` is now nullable internally,
because a default nobody chose and a value somebody wrote have to be distinguishable.

`ModelPolicy` overrides the policy per model, because one credential often serves models an operator
feels differently about. The alternative is declaring the same vendor twice and copying the key, and
a credential written down twice is a credential rotated once. **A `ModelPolicy` naming a model the
`ModelMap` does not carry fails startup**: a policy without a mapping is a route that does not exist.

## The backstop is the policy's answer, not the error's

When an upstream call fails, whether a node may catch it is decided by what the operator asked for:

- `prefer` and a saturation burst **may** fall back to a node. Falling back to the local fleet is not
  a second disclosure of the prompt, so it can happen quietly.
- `only` and a request that named a provider by header **may not**, and get a `502` naming the
  situation. Answering from different weights than the caller asked for, silently, is the one failure
  that looks like a success — and `only` exists precisely for the case where a local model shares a
  name with a vendor's.

## The steer can only ever narrow

`X-InferHub-Provider: <id>` serves from that provider **iff it already claims the model**; otherwise
`400`, and nothing leaves the hub. A header can never create a route the configuration does not
already contain — the track's consent rule, unweakened.

`X-InferHub-Provider: node` refuses every provider for one request, including an `only` one. That is
the direction that matters: it is how somebody keeps a single prompt off a vendor's servers without
an operator editing config, and it costs nothing on a hub that has no providers at all.

**A wrong steer gets one sentence** — the same one for an unknown id, a parked provider and a real
provider that maps something else. Three mistakes, one answer, so a client holding an inference key
cannot enumerate your vendor configuration by probing. `/api/status` answers that question and is
admin-gated. `fallback` is a header *value*, not a steerable id.

The steer is a header rather than a body field on purpose: the body is forwarded to the upstream
verbatim, and a routing directive sitting inside a payload is a field a vendor will one day interpret
as its own.

## One discovery surface

`/api/tags` and `/v1/models` now list the models a **named** provider claims, merged with the fleet's.
Until today a mapped model no node held was a model a client **could not discover and could call** —
the two endpoints said one thing and `/api/chat` did another.

- A name both hold appears **once**, and the node's entry wins: `digest` and `size` are facts about a
  file on a box.
- A provider-only entry carries **null** for both rather than a zero somebody would later read as a
  measurement, and `["chat"]` for capabilities — `EmbeddingDispatcher` has no provider arm, so
  listing `embed` would be a promise answered with a 404.
- **No vendor is named.** `owned_by` stays `inferhub`; `X-InferHub-Served-By` still answers "who
  served *this*" after the fact. A listing that named vendors would turn your configuration into a
  fact every client with a key can read.
- Models mapped by the legacy `Fallback:` section are **deliberately absent**, exactly as they are
  from `/api/status`'s `providers` array — which is what keeps a v3.28-configured hub identical.

## One breaking change, on a three-release-old field

`/api/status`'s `providers[]` reports **`policy`** and no longer reports `trigger`. Two spellings of
one thing on a status payload is how a dashboard ends up believing whichever key it read first. The
array is `null` for every deployment that never wrote a `Providers:` block, and it has no console
panel until v3.34 — so this is the cheapest the rename will ever be. `modelPolicies` appears only
where overrides exist.

## Tests

`tests/InferHub.Tests.Coordinator` (713 passed, 43 skipped) and `tests/InferHub.Tests.Mesh`
(402 passed, 2 skipped), plus the full solution as CI runs it: **1 439 passed, 48 skipped, 0 failed.**

The mesh suite is the one that earns its keep. `ProviderRoutingTests` runs the shipped hub on real
Kestrel with a **real** `ProviderDispatcher` and a **real** `HttpClient`, against a **second Kestrel**
standing in for the vendor and recording what arrived. A stubbed dispatcher can only confirm what the
author already believed; this one answers the question the phase actually asks — *where did the prompt
go* — and it carries its own guard, `AnOverflowProviderIsStillTheSecondChoice`, so the preferred case
is measuring the policy rather than the fixture.

## What was not established

- **That any real vendor honours a preferred route under load.** No test in this repository calls a
  live provider, by design — a test needing somebody's API key is a test CI cannot run and a card
  billed on every commit. That is phase 68's day, with one real key per provider, driven by hand.
- **Embeddings still never reach a provider.** Gemini's `EmbedAsync` is real and unit-tested and
  nothing on the hub routes to it; that is v3.35's job, and the discovery surface says `chat` rather
  than pretending otherwise.
- **The per-frame error-envelope parse still wants v3.30's substring guard.** Recorded in v3.32 as a
  cost rather than a defect; it did not go in here either, and it is now v3.34's.

Zero new `PackageReference`s. `InferHub.Shared.csproj` is still an empty `<Project Sdk="…">`.

---

## Addendum — the published image, checked the same evening

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.33.0`, pulled and driven by hand. The image's own
label reports `version=3.33.0`, `revision=675451a` — the phase commit, asked of the artefact rather
than of a dashboard. A stub vendor ran on the host and recorded every request that reached it.

A hub with one `openai-compatible` provider at `Policy: prefer`, mapping `smart` and `cloud-only`,
**and no node at all**:

1. **`/api/tags`** — `[{"name":"cloud-only","digest":null,"size":null},{"name":"smart",…}]`. Both
   provider-claimed models listed, both with a genuine `null` rather than a constructed zero.
2. **`/v1/models`** — the same two, `owned_by: inferhub`, `capabilities: ["chat"]` on each. No vendor
   named on either surface.
3. **A plain chat for `smart`** — `200`, `X-InferHub-Served-By: provider:vendor`, *From the vendor.*,
   and the stub recorded `Bearer vendor-key` with `"model":"remote-smart"`. Asked by the vendor's
   name for it, answered in the caller's.
4. **`X-InferHub-Provider: node`** — `404 model 'smart' not found`, which is the fleet's own answer.
   The provider was refused for that request.
5. **`X-InferHub-Provider: not-configured`** — `400`, and the sentence names the pair the caller typed
   and **nothing else**. The word `vendor` does not appear in it.
6. **The stub's hit counter stayed at 1** across 4 and 5. Both refusals happened before anything left
   the hub — the assertion the response codes alone cannot make.
7. **`/api/status`** — `"policy":"prefer"`, `"modelPolicies":null`, and **no `trigger` key** in the
   providers block. The `fallback` block still carries its own `trigger`, untouched.
8. **`Policy: prefer` + `Trigger: no-node-or-saturated`** — the container refuses to start:
   *"Providers:vendor sets Policy 'prefer' and Trigger 'no-node-or-saturated'…"*
9. **`ModelPolicy:typo` with no such mapping**, and **`Policy: always`** — both refuse to start, each
   naming what it wanted. The second lists all four policies.
10. **A `Fallback:`-only hub on the same image** — `/api/tags` is `{"models":[]}` (the legacy
    section's mapped model is deliberately **not** discoverable), `/api/status` has **no `providers`
    key at all**, and a chat still answers `X-InferHub-Served-By: fallback`. The invariant this phase
    was likeliest to break, checked on the artefact rather than argued.

**What this run did not establish, and could not:** that `prefer` beats a *live* node. Every check
above ran on a hub with an empty fleet, where `prefer` and `no-node` are indistinguishable by
observation — the distinction is made in `ProviderRoutingTests`, over a real socket, against a hub
whose router always returns a node. Proving it on a published image needs a registered node holding
the model, which is the shape of phase 68's day rather than of an evening's check.
