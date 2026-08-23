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

## The published image, checked — and it found a defect

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.32.0`, pulled ~8 minutes after the tag (label
`3.32.0`, revision `d963451`), run against a local stub answering Gemini's documented shapes. **Two
of the three checks passed and the third found a real hole**, which is what this step is for.

**What the stub received** — the model is a path segment, `systemInstruction` is lifted, the
thinking budget travels, and there is no `model` field in the body at all:

```
POST /v1beta/models/gemini-3-pro:generateContent
    x-goog-api-key: AIza-stub-key
    BODY {"contents":[{"role":"user","parts":[{"text":"hi"}]}],
          "systemInstruction":{"parts":[{"text":"Be terse."}]},
          "generationConfig":{"thinkingConfig":{"thinkingBudget":0}}}
```

No `Authorization` header. D2 and D6, observed on the artifact rather than asserted.

**Blocking**, against `promptTokenCount: 165` / `cachedContentTokenCount: 140` /
`candidatesTokenCount: 7` / `thoughtsTokenCount: 12`:

```
X-InferHub-Served-By: provider:gemini
{"model":"big",…,"message":{"content":"Hello from the stub."},"done":true,
 "prompt_eval_count":165,"eval_count":7,"done_reason":"stop"}
```

**165, not 305** — the cached tokens were left inside the prompt count. **7, not 19** — the thinking
tokens were not folded into the answer's. D5 and D6, on the thing that shipped.

**Streaming**, against frames carrying `candidatesTokenCount` of 2, then 9, then 15:

```
{"message":{"content":"Hello"},"done":false}
{"message":{"content":" from the stub."},"done":false}
{"message":{"content":""},"done":true,"prompt_eval_count":165,"eval_count":15,"done_reason":"stop"}
```

**`eval_count` is 15, not 26.** Taken, not summed — D4 proven on the artifact, and the URL carried
`:streamGenerateContent?alt=sse`.

**A blocked prompt, blocking:** `502`, carrying
`the Gemini upstream refused the prompt before the model saw it (blockReason: PROHIBITED_CONTENT)`.

**A blocked prompt, streaming — and this is the defect.** When the stub answered the *streaming*
endpoint with the block as an ordinary JSON body rather than as an SSE frame, the hub returned:

```
{"model":"refused",…,"message":{"content":""},"done":true,"done_reason":"stop"}
```

**An empty answer marked finished.** Every line of a body with no `data:` prefix was skipped, the
loop ended, and the terminal chunk went out. D7 caught this shape when it arrives *as a frame* — a
second run with the block wrapped in `data:` produced the correct terminal error chunk — and had
nothing to say about the identical body unframed. **Fixed in v3.32.1** (see below). This is the
fourth "200 that looks finished" in this track and the first one that shipped.

**An embedding request** returned `404 no node is advertising embedding model 'embed'`. That is
correct behaviour and a **documentation error**: `/api/embed` goes to `EmbeddingDispatcher` and the
fleet, never to `ProviderDispatcher`, so Gemini's real `EmbedAsync` is not reachable from the hub
until phase 67. The notes above, the README and the site all implied otherwise. Also corrected in
v3.32.1.

## v3.32.1 — what running the image found

Two things, in one patch, neither of which any unit test was going to catch:

**1. A streaming response that is not SSE at all now throws instead of finishing empty.**
`GeminiUpstreamClient.NotSse`: if a streamed response contained no `data:` line, the body is kept
(bounded at 64 KB) and the failure names what actually arrived — a **block delivered before the
stream started**, with its `blockReason` intact, or a **JSON array**, which is what
`:streamGenerateContent` returns when `alt=sse` does not reach the vendor. This client always sends
that query, so reaching the second case means something in between dropped it, and the operator is
told exactly that.

**This also corrects 64 D3's own sentence.** As shipped it read: without `alt=sse` "a reader written
for SSE does not fail — it *waits*, until the timeout." It did not wait. It answered immediately and
wrongly, which is worse than hanging. The sentence is corrected in `src/InferHub.Shared/CLAUDE.md`,
in the code, in the README and on the site rather than left standing.

**2. The embeddings claim is corrected everywhere.** Gemini's `EmbedAsync` is implemented and
unit-tested; the hub does not route to it. 63 D7 already said this about Anthropic's 501 and it
applies to both — the difference is only that Anthropic has no such API and Gemini does.

Two new tests in `GeminiDialectTests`, and **both were confirmed to fail without the fix** before it
went in. `tests/InferHub.Tests.Shared` **170 passed**, `tests/InferHub.Tests.Coordinator` **690
passed / 43 skipped**.

### The patched image, checked in turn

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.32.1` (label `3.32.1`, revision `d005a18`), same
stub, three cases:

```
block as a PLAIN body on the streaming endpoint  (this is the defect)
  {"error":"the Gemini upstream refused the prompt before the model saw it
            (blockReason: PROHIBITED_CONTENT); no answer was generated","done":true}

block as an SSE FRAME                            (must not have regressed)
  {"error":"the Gemini upstream refused the prompt before the model saw it
            (blockReason: PROHIBITED_CONTENT); no answer was generated","done":true}

an ordinary stream                               (must not have regressed)
  {"model":"big",…,"done":true,"prompt_eval_count":165,"eval_count":15,"done_reason":"stop"}
```

The first is the fix — that exact request returned an empty answer marked `done` on 3.32.0. The
second and third are the guard on the fix: the framed path and the happy path are unchanged, and
`eval_count` is still 15 rather than 26.

**Not checked on the image: the JSON-array case** (`alt=sse` stripped in transit). It travels the
same `NotSse` path and is covered by a unit test, but no stub reproduced it end to end — said out
loud rather than implied by the other two passing.

## Known and deliberately not fixed tonight

`ReadTextAsync` attempts an error-envelope deserialization on **every** `data:` frame, including
ordinary token deltas. It is correct — a candidate frame has no `error` property, so it yields null
— but it is a parse per token, and **62 D4 put a substring guard in front of exactly this check on
the OpenAI path for exactly this reason.** The Gemini path should have the same guard, plus 62's
"guard on the guard" test (a delta whose *content* mentions the word must not become a 502).

Not done here: v3.32.1 is tagged, this is a cost rather than a defect, and a third tag in an evening
for a micro-optimisation is the kind of churn this project's one-release-per-phase discipline exists
to avoid. Recorded so it is a decision rather than an oversight; it goes in with 65 or 66.

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
