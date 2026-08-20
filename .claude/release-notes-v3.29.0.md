# v3.29.0 — the cloud upstream gets a name, and there can be more than one

Phase 61, the first of the v3.29–v3.36 provider track. Since v2.4 this coordinator has had exactly
one cloud upstream and it has had no name: one `BaseUrl`, one `ApiKey`, one `ModelMap`. A deployment
holding an OpenAI key *and* an OpenRouter key could not express it, and neither could one that wanted
`llama3` bursting to one place and `big-code` to another.

`Providers:` is that feature with an id on each one. Everything the `Fallback:` warning promises is
unchanged — off unless configured, mapped models only, nothing stored, every response tagged — and
**a deployment that changes no config behaves byte-identically to v3.28**, headers and `/api/status`
payload included.

```jsonc
{
  "Providers": {
    "openai":     { "BaseUrl": "https://api.openai.com/v1",
                    "ModelMap": { "llama3": "gpt-4o-mini" } },
    "openrouter": { "BaseUrl": "https://openrouter.ai/api/v1",
                    "Trigger": "no-node-or-saturated",
                    "ModelMap": { "big-code": "qwen/qwen3-coder" } }
  }
}
```

Credentials go in the environment (`Providers__openrouter__ApiKey`), never in the file.

## One model is claimed by exactly one enabled provider

Two of them mapping the same model **fails startup**, naming the model and both providers. The
projected `Fallback:` section counts as a claimant, so the collision an upgrade would create is
caught on the upgrade rather than the first time that model is asked for.

**Considered and rejected: first match in declaration order.** It is what most gateways do, it always
works, and it makes the single most consequential choice in this product — whose servers see
somebody's prompt — depend on JSON key ordering surviving three layers of configuration binding.
**Considered and rejected: reading a duplicate as a failover pair.** Retrying a prompt at a second
vendor because the first refused it is a second disclosure of that prompt; it is deferred at the
track level with its own argument, and it must not arrive by typo.

An unknown `Type` fails startup too, naming the types that exist (`openai-compatible` today;
Anthropic is v3.31 and Gemini v3.32). A provider that failed to be understood must not become a
request that fails at dispatch, hours later, in front of a user.

## `Fallback:` is projected, not kept beside

The legacy section becomes one more entry in the registry at resolve time, so there is **one dispatch
path** rather than a new one and an old one. The branch nobody is developing is the one that keeps
working right up until it silently does not — and the projection is what lets "changes no config ⇒
behaves identically" be asserted against the *new* code instead of a detour around it.
`FallbackTests` is unchanged in intent and is now exactly that assertion.

## What the wire says

- `X-InferHub-Served-By: provider:<id>` for a named provider; still `fallback` for the legacy
  section. A header that quietly changes meaning is worse than one with two spellings.
- `inferhub_fallback_dispatched_total` keeps counting **every** provider dispatch — it has always
  meant "requests the fleet did not serve", which is exactly what it still counts — with
  `inferhub_provider_dispatched_total{provider}` and `inferhub_provider_last_model{provider,model}`
  beside it as the attribution. A provider that has served nothing emits **no series** (phase-28 D5).
- `/api/status` gains a `providers` array, **omitted entirely** when none is configured. It reports a
  configured provider before it has served anything — 22 D5's rule that "is this thing sending my
  prompts anywhere" is answerable from the status page — and its `credential` reads `configured` or
  `absent`, never a prefix, a length or a hash of the key.
- The trigger moved onto the provider. Whether you want *this* upstream when the fleet is merely busy
  is a question about that upstream's price and latency; one global answer forces the expensive
  vendor and the cheap one into one policy.

## Under it, a seam for the next four phases

`IUpstreamDialect` (`src/InferHub.Shared/Upstream/`) — Ollama JSON in, Ollama JSON out, five members,
no ASP.NET — implemented by `OpenAiUpstreamClient`, which already had that shape because both ends of
the mesh drive it (22 D1). Phases 63 and 64 add Anthropic's `/v1/messages` and Gemini's
`:generateContent` behind it without touching a dispatcher, a router or an endpoint, and phase 67
gives the node the same four providers by composing the same clients rather than writing a second
set. Internally the types now say `Provider` (`IProviderDispatcher`, `ProviderDispatcher`,
`ProviderResult`); the config section, the header value, the status key and every metric name did
not move.

## Verification

`dotnet test tests/InferHub.Tests.Coordinator` — **667 passed, 43 skipped**, up from 649 (18 new).
The full solution was also run once, because the rename crosses every project: **1 346 passed, 48
skipped**. `ContextContractTests` green, including the budget this phase spent. Per the track's D6, **no test in this repository calls a live provider**: the dialects are
driven against a recording stub that captures the URL, the `Authorization` header and the body.

Run from source and checked against the process, because a configuration binder is precisely what a
unit test does not exercise:

- Two providers set via `Providers__openrouter__*` / `Providers__vllm__*` bound correctly, each with
  its own trigger, and `/api/status` reported both — `credential: "configured"` for the one with a
  key and `"absent"` for the one without.
- The key `sk-or-v1-secret-value` appears **nowhere** in the status payload.
- With no `Providers:` section the `providers` key is **absent** from `/api/status`, not null.
- Two providers mapping `llama3` refused to start:
  `OptionsValidationException: model 'llama3' is mapped by both Providers:a and Providers:b.`

## What was not established, said out loud

- **No live provider was called.** No OpenAI, OpenRouter, Anthropic or Gemini endpoint has seen a
  request from this code. That is deliberate and it is phase 68's job, with one real key each.
- **The published image was not pulled at the time of writing** — the tag had not built yet. The
  from-source run above covers the same binder and the same payload, and the image check follows.
- **No routing behaviour changed.** A provider is still consulted only after the router found no node
  (or, under `no-node-or-saturated`, when every capable node is at its declared cap). Preferring a
  provider while the fleet is up is v3.33.
- **The usage ledger did not change.** Provider traffic still records `fallback: true` and no
  provider id: that is a Postgres migration on `usage_records`, and it belongs with the phase that
  gives it a reader. Nothing reads a provider's token counts yet either.
- **One 502 sentence still says "the fallback upstream failed"** even for a named provider. It is a
  client-visible string and changing it for the legacy case would have broken the byte-identity this
  release claims; naming the provider there is a small piece of v3.33's work.
- **`src/InferHub.Coordinator/CLAUDE.md` is at 1100 of its 1100-line budget.** This phase paid for
  its own block by compressing two paragraphs 22 D4/D5 had made stale. **Phase 62 cannot add a line
  to that file without splitting it**, which is the budget doing its job (52 D5) and is written here
  so the next phase meets it as a decision rather than a red test.
