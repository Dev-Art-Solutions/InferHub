# v3.30.0 — OpenRouter, and the two things a compatible dialect was quietly getting wrong

Phase 62, the second of the v3.29–v3.36 provider track. You could already reach OpenRouter in
v3.29: `Type: "openai-compatible"`, their base URL, a key, a map. That is exactly what the track
promised — **OpenRouter is the OpenAI dialect** — and it is also why this release exists, because
everything about them that is *not* the dialect was either invisible or wrong.

```jsonc
"Providers": {
  "openrouter": {
    "Type": "openrouter",                       // BaseUrl defaulted; still overridable
    "Referer": "https://mesh.example.com",      // both optional, both absent by default
    "Title": "Example mesh",
    "ModelMap": { "big-code": "qwen/qwen3-coder", "fast": "~openai/gpt-mini-latest" }
  }
}
```

`Providers__openrouter__ApiKey` in the environment, as before. **A deployment that changes no config
behaves byte-identically to v3.29**, headers and `/api/status` payload included.

## What the type buys, given it buys no dialect

`ProviderDispatcher` hands `openrouter` and `openai-compatible` to the *same*
`OpenAiUpstreamClient`. The identity is the claim, not an implementation detail. What the type adds
is the configuration around it:

- **A base URL you need not type.** `https://openrouter.ai/api/v1` by default, and `BaseUrl` still
  overrides it — a proxy in front of a vendor is a deployment somebody has, and a default that
  cannot be replaced is a wall.
- **The model map is checked at startup** against OpenRouter's id shape — `vendor/model`, optional
  `~` prefix for a floating alias, optional `:free` / `:batch` / `:thinking` suffix. `gpt-4o-mini`
  is a real OpenAI id and has never been an OpenRouter one; left to run it is a `400` discovered
  weeks later, on the one request the fleet could not serve.
- **Attribution headers that this hub never fills in for you.** `Referer` → `HTTP-Referer` and
  `Title` → `X-OpenRouter-Title` list an app on OpenRouter's **public** rankings. Defaulting them to
  this product's own name and URL would be free marketing paid for with somebody else's deployment
  appearing on a vendor's public page because they configured a model.

**Considered and rejected: a generic `Headers:` map on every provider.** It is the smaller diff, it
is a place for an operator to put a second `Authorization` beside the one this code sets, and it
leaves "which vendor is this" unanswerable in the `type` field `/api/status` already reports.

The id check is a shape check and nothing else. **Rejected: validating against OpenRouter's live
`/models` listing** — booting would depend on a vendor being up, and the track's rule is that a
listing may inform a console and may never create a route. **Rejected: a checked-in list of
vendors**, which is 48 D1's "usually right" in its purest form. *The risk that leaves, stated rather
than mitigated: the day an unnamespaced id ships there, this refuses a valid configuration.* It is a
one-line fix behind a message that says what it wanted, and it is the trade an unknown `Type` and a
doubly-claimed model already make.

## Two bugs that were never about OpenRouter

Both live for **every** OpenAI-compatible upstream since v2.4, and both found by reading the
provider's current documentation rather than assuming the dialect matched.

**`error.code` is a number at OpenRouter and a string at OpenAI.** `OpenAiErrorBody.Code` was
`string?`, so deserializing the envelope threw, `Describe` caught its own `JsonException` and fell
back to the **raw body** — the one sentence saying what to fix, delivered buried in the JSON it
arrived in. That is phase-29 D6's wall of backslashes, reached by another route. A three-line
converter reads both. **Rejected: a second envelope for the servers that do it** — the field is
spelled `code` in both and means the same thing; what differs is a JSON scalar type, which is not a
schema.

**An error that arrives after the response headers ended the stream quietly.** OpenRouter reports
one as an SSE frame carrying a top-level `error` and `finish_reason: "error"`; the parser read that
as an ordinary terminal delta, so a request that died at token 40 returned **200 and looked
finished**. It now throws mid-iteration, which both callers already handle — the hub's
`ProviderDispatcher` writes a terminal error chunk and the node's `OpenAiBackend` carries it back as
a failed job. **Rejected: yielding an Ollama error chunk from the dialect** — same destination, and
it makes the contract "an error is a value here and an exception there".

The status is the upstream's own when it named a plausible HTTP one, and 502 otherwise. Nothing is
inferred from the error *text* — 29 D6 unmoved.

## The context files were split, because the budget said so

`src/InferHub.Coordinator/CLAUDE.md` was at **1100 of 1100** and v3.29.0's notes said phase 62 would
meet that as a decision rather than a red test. The cut is the directory axis: phases **31, 35 and
44** — client-scoped collections, Qdrant in production, hub-assigned retrieval — moved **whole** into
a new `src/InferHub.Coordinator/Vector/CLAUDE.md` beside the code they constrain, with the three
providers' anatomy. **1100 → 904.** **Rejected: compressing the phase-22 cloud-burst block**, the
tempting one because v3.29 replaced its mechanism, but its arguments are all still true and
shortening a record to buy space for a newer record is how a file loses the paragraph that explains
itself. **Rejected: raising the budget**, which is what a budget exists to be argued against.

`ContextContractTests` is the proof that nothing was lost in the move: every decision block that
existed still exists, in exactly one file.

## Verification

`dotnet test tests/InferHub.Tests.Coordinator` — **679 passed, 43 skipped**, up from 667 (12 new).
`tests/InferHub.Tests.Shared` — **134 passed, 0 skipped**, up from 129 (5 new), including the new
budget row. Full solution: **1 363 passed, 48 skipped**, up from 1 346.

Per the track's D6, **no test in this repository calls a live provider**: both dialect fixes are
driven against recorded payloads — a captured OpenRouter 429 with a numeric `code` and a captured
mid-stream `error` frame. Both were confirmed to **fail without the fix** before being kept.

Run from source against a raw TCP listener standing in for the upstream, because a configuration
binder and an outgoing header set are precisely what a unit test does not exercise:

- `Providers__or__*` bound an `openrouter` provider with **no `BaseUrl`**, and `/api/status`
  reported `"type": "openrouter"`, `"credential": "configured"`.
- The key `sk-or-v1-secret-value` appears **nowhere** in the status payload.
- One burst reached the stub carrying, on the wire:
  `Authorization: Bearer sk-or-v1-secret-value`, `HTTP-Referer: https://mesh.example.com`,
  `X-OpenRouter-Title: Example mesh`, and `{"model":"qwen/qwen3-coder",…}` — the local name rewritten
  to the upstream one. The response came back as `big-code` with `X-InferHub-Served-By: provider:or`
  and the upstream's token counts.
- The same provider with **no** `Referer` and no `Title` sent **neither header**. Absent is absent.
- `ModelMap: { "big-code": "gpt-4o-mini" }` under `Type: openrouter` refused to start:
  *"Providers:or:ModelMap:big-code is 'gpt-4o-mini', which is not an OpenRouter model id…"* — and it
  got that far with no `BaseUrl` configured, which is the default URL satisfying the earlier check.

## What was not established, said out loud

- **No live provider was called.** No OpenRouter endpoint has seen a request from this code. That is
  deliberate and it is phase 68's job, with one real key each. In particular, the default base URL
  was verified as *bound and accepted*, not as *reachable*.
- **Nothing reads `usage.cost`.** OpenRouter returns charges in credits on every response and this
  release ignores them. Per-token cost accounting stays deferred: a number this hub did not measure
  does not belong in the same column as ones it did. Token counts *are* read, because the dialect
  never stopped reading `usage`.
- **`openrouter/auto` works and the hub cannot tell you which model answered.** The dialect
  translates the response against the model that was *sent*, so the resolved id is discarded before
  anything could report it. Relatedly, OpenRouter's own choice of backing host for a model is the
  vendor's routing, not this hub's — nothing here turns it on or off, and its `provider` routing
  block is not exposed.
- **No routing behaviour changed.** A provider is still consulted only after the router found no
  node (or, under `no-node-or-saturated`, when every capable node is at its declared cap).
  Preferring a provider while the fleet is up is v3.33.
- **The usage ledger did not change.** Provider traffic still records `fallback: true` and no
  provider id.
- **The 502 sentence still says "the fallback upstream failed"** even for a named provider —
  unchanged from v3.29, and still a piece of v3.33's work.
- **The published image was not pulled at the time of writing.** The from-source run above covers the
  same binder and the same outgoing headers; the image check follows.
- **Nothing on the node changed.** `Backend:Type=openai` gets both dialect fixes for free, because
  they landed in `InferHub.Shared` — but it does not get the `Providers:` map. That is phase 67.
