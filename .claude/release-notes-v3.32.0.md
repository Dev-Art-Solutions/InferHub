# v3.32.0 — Gemini's own `:generateContent`, and the day the native dialect turned out to be the legacy one

Phase 64, the fourth release in the v3.29–v3.36 provider track and the third real dialect behind the
seam. You could already reach Gemini in v3.31 through their OpenAI-compatibility endpoint; what you
got back was that layer's idea of your usage and your errors, which is the reason this release
exists — the same reason v3.31 gave for Anthropic.

```jsonc
"Providers": {
  "gemini": {
    "Type": "gemini",             // BaseUrl defaulted to .../v1beta; overridable
    "ThinkingBudget": 0,          // optional; leave it out to keep the model's dynamic default
    "ModelMap": { "big": "gemini-3-pro", "embed": "models/gemini-embedding-001" }
  }
}
```

`Providers__gemini__ApiKey` in the environment. **A deployment that changes no config behaves
byte-identically to v3.31**, headers and `/api/status` payload included.

## The thing this release found, and it is not a bug

The track was written in August believing `:generateContent` was Gemini's native API. Reading the
docs on the day — 57's precedent, deliberately repeated — found that **it is the *previous* native
API.** Google's current documentation calls the **Interactions API** generally available and
recommended for new projects, says "all new models, multimodal capabilities, tools, and agentic
features will launch" there, and says of the older surface: *"While it is now considered legacy, the
original `generateContent` API remains fully supported."* **No removal date is published.**

The phase went ahead with `:generateContent` on three grounds, and they are worth stating because
the obvious reading of "legacy" is that we picked the wrong one:

1. **It is the surface more than one platform shares.** The same `:generateContent` /
   `:streamGenerateContent` verbs are Vertex AI's, and every proxy and gateway in between speaks
   them. A `BaseUrl` override reaching those is exactly the configuration v3.31 refused to break by
   declining to shape-check Anthropic ids. The Interactions API is first-party-only today.
2. **What Interactions adds is precisely what this project defers.** Its `steps[]` of thoughts,
   function calls and tool results collapses to one text output for a mesh whose job protocol is
   Ollama-shaped, and its server-side `previous_interaction_id` is conversation state a hub that
   retains nothing must not own. We would pay for the richer surface and discard the part that makes
   it rich.
3. **It changed its own schema three months ago** — `outputs[]` became `steps[]` under an
   `Api-Revision` date header, with the old schema removed on 2026-06-08. A dialect whose stability
   depends on pinning a date, and which is tested only against recorded payloads, goes stale in
   silence.

**Said out loud rather than hedged:** the day InferHub wants Gemini's agentic surface, *this* client
is not the one that gets extended. That is a `Type: "gemini-interactions"` and a phase of its own.

## Six wire facts, read from the docs on the day

1. **`promptTokenCount` already includes the cached tokens**, with `cachedContentTokenCount` as a
   *breakdown* of it. Anthropic's `cache_read_input_tokens` is a *sibling* of `input_tokens`. So the
   identical-looking arithmetic is wrong in **opposite directions** — adding Gemini's double-counts,
   adding Anthropic's invents a total. Neither is adjusted: each dialect reports what its vendor
   calls the prompt, and these two fields are the evidence that "just add them up" is not a rule.
2. **Thinking is on by default, `thoughtsTokenCount` is separate from the answer's count, and both
   are billed as output.** `eval_count` carries `candidatesTokenCount` alone, because a client
   reading that field means *"tokens in the answer I received"*. So your invoice's output figure is
   the larger of the two, and `ThinkingBudget: 0` is the lever that makes them agree rather than an
   arithmetic that hides the gap.
3. **Streaming `usageMetadata` is cumulative** — every chunk carries the whole thing as it stands.
   Second vendor running, after Anthropic, so the house rule is now stated: *read a provider's usage
   as a snapshot unless it documents an increment.*
4. **Without `?alt=sse`, `:streamGenerateContent` is not SSE at all** — it answers with a chunked
   JSON array and never emits a `data:` line. A reader written for SSE does not fail; it **waits**,
   until the request timeout. The query is a constant beside the verb, not a setting.
5. **A blocked prompt is a 200 with no candidates** and a `promptFeedback.blockReason`. The third
   success status in this track that is not one, after v3.30's mid-stream `error` frame and v3.31's
   `overloaded_error`. It throws and names the reason.
6. **`error.code` is a number**, with a canonical `status` (`RESOURCE_EXHAUSTED`) and a `details[]`
   that can carry a `RetryInfo.retryDelay`. The numeric code is the exact shape that broke the
   OpenAI dialect for eight releases until v3.30 found it — typed as a number here from the first
   line rather than discovered twice. `status` and `retryDelay` are carried through for the reason
   v3.31 carried Anthropic's `request_id`: they are what the operator actually needs.

## Two things that are *not* like Anthropic

- **Nothing is dropped from the sampling options.** Gemini's `generationConfig` has `seed`,
  `presencePenalty` and `frequencyPenalty` — the three v3.31 had to drop — alongside `temperature`,
  `topP`, `topK` and `stopSequences`. That drop was a fact about Anthropic, not a policy here.
- **Embeddings work.** This is the first provider dialect whose `EmbedAsync` is real rather than a
  501. It uses `:batchEmbedContents` for a single input as well as forty, and sends **no
  `taskType`**: `RETRIEVAL_DOCUMENT` and `RETRIEVAL_QUERY` produce better vectors than the default,
  and nothing in an embed request says which one the caller meant — one value per provider would be
  wrong for half the traffic of any deployment that both ingests and searches.

## The model id is normalized, not checked — and that completes a three-way contrast

Gemini's model is a **URL path segment**, not a body field. One rule covers all three legal forms:
an id containing a `/` is a path and is used as written; a bare id gets the `models/` prefix. So
`gemini-3-pro`, the `models/gemini-3-pro` that `GET /v1beta/models` hands back, and a Vertex-style
`publishers/google/models/…` all work, and `ListModelIdsAsync` returns the vendor's own `name` so a
console row is pasteable into a `ModelMap` unedited.

The three phases now say three different things about model ids, and the rule they add up to is
worth keeping: **check where the vendor's namespace is evidence** (v3.30, 419-of-419 ids carry a
slash), **check nothing where one model has several legitimate spellings** (v3.31), **normalize
where the id is structural** (here — getting it wrong produces a 404 naming
`models/models/gemini-3-pro`, which nobody typed).

There is also no `MaxTokens` for `gemini`: that vendor does not require a ceiling, so imposing a
declared default would truncate answers that would otherwise finish. `maxOutputTokens` travels only
when a caller sends `options.num_predict`.

## Tests

`tests/InferHub.Tests.Shared` **168 passed**, `tests/InferHub.Tests.Coordinator` **690 passed, 43
skipped** (the gated Postgres integration tests). 22 of the Shared tests are new
(`GeminiDialectTests`), 5 of the Coordinator ones.

**The two load-bearing assertions were confirmed to fail without their fix first**, which is v3.30's
discipline: patching the stream state to *sum* the cumulative usage, and removing the blocked-prompt
throw, each broke exactly one test and nothing else.

**Zero new `PackageReference`.** `InferHub.Shared.csproj` is still an empty `<Project Sdk="…">`, for
the third dialect running.

## The published image, checked

*(Filled in after the tag — see the addendum below.)*

## What was not established, said out loud

- **No live Gemini endpoint was called.** Every assertion in this release is against a **recorded
  payload** — the shapes the vendor's documentation carries, captured on the day. That is the
  track's own rule (a test needing somebody's API key is a test CI cannot run, so it becomes a test
  everyone skips, and it bills a card on every commit). The first real key is phase 68's, and until
  then *the risk that Google's wire format differs from its documentation is accepted, not
  mitigated.*
- **Not translated:** tools and function calling, `responseSchema` / structured outputs, thought
  summaries (`includeThoughts` is never sent; a thought part arriving is skipped), context caching,
  the file APIs, and the image/video surfaces. All track-level deferrals.
- **`ThinkingBudget` was not verified against a model that honours it.** The field is sent when set
  and absent when not — that much is asserted on the wire — but whether a given model actually stops
  reasoning at `0` is the vendor's behaviour and belongs to phase 68.
- **`GET /v1beta/models` was exercised by unit test only**, as was the `/api/generate` path and the
  `inlineData` image translation.
- **The node is unchanged by this phase** and its image was not pulled. A node that speaks Gemini is
  phase 67.
- **Providers are still consulted only when the fleet cannot serve.** Routing a model to one *by
  preference* is phase 65 (v3.33). This release changed who the upstream can be, not when it is
  asked.
