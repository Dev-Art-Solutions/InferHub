# v3.31.0 — Anthropic's own `/v1/messages`, and the four wire facts that were read rather than recalled

Phase 63, the third of the v3.29–v3.36 provider track and the first release in it that adds a real
second dialect. You could already reach Anthropic in v3.30: `Type: "openai-compatible"`, their
compatibility base URL, a key, a map. What you got back was that layer's idea of your usage and
your errors, which is the whole reason this release exists.

```jsonc
"Providers": {
  "claude": {
    "Type": "anthropic",          // BaseUrl and anthropic-version defaulted; both overridable
    "MaxTokens": 4096,            // the ceiling used when a caller sends no options.num_predict
    "ModelMap": { "big": "claude-opus-5", "cheap": "claude-haiku-4-5" }
  }
}
```

`Providers__claude__ApiKey` in the environment. **A deployment that changes no config behaves
byte-identically to v3.30**, headers and `/api/status` payload included.

## Why a second dialect rather than a second base URL

Three things about Anthropic are not the OpenAI dialect, and each is a reason on its own:

- **The token counts.** Anthropic reports `input_tokens`, `output_tokens`,
  `cache_creation_input_tokens` and `cache_read_input_tokens` as four separate numbers. The
  compatibility layer flattens them into two, `/api/admin/usage` then disagrees with the invoice,
  and the first question anybody asks is why.
- **The errors.** Their envelope is `{"type":"error","error":{…},"request_id":"req_…"}` — and the
  `request_id` is the identifier their support asks for. It used to be thrown away at the boundary.
- **The credential.** `x-api-key` plus a required `anthropic-version` header. A Bearer token there
  is a 401 that reads like a bad key, and `OpenAiUpstreamClient.Configure` can only send one.

`AnthropicUpstreamClient` implements the same `IUpstreamDialect` phase 61 extracted — Ollama JSON
in, Ollama JSON out, five members — so `ProviderDispatcher` gained **one arm** and the router, the
endpoints, the affinity keys and the retrieval pipeline gained nothing. That is 61 D3 paying for
itself the first time, and it is the release's actual claim.

**Considered and rejected: renaming the `OpenAi*` exception family while in here.** 57 D10 refused
that rename for the reason that still holds — a phase moving a hundred call sites has a bisect
nobody can read. `UpstreamDialectException` is a **base** instead, so both suites asserting
`OpenAiUpstreamException` pass untouched.

## Four wire facts, read from the docs on the day. Two contradicted the plan's first draft.

57's precedent, deliberately repeated: read the vendor's current documentation before writing a line.

1. **`message_delta` usage is cumulative** — the vendor's docs say so in a warning box. And
   `message_start` already carries `output_tokens: 1` before a single token has been produced. So
   counts are **taken**, never summed; a dialect that added the frames would report 24 tokens for a
   15-token answer, and an invented number sitting in the column beside measured ones is how a
   usage report stops being evidence.
2. **There is no `[DONE]` sentinel.** The stream ends at `message_stop`. `ReadFramesAsync` is
   therefore not reused — its sentinel never arrives and its error envelope is the wrong one.
3. **`max_tokens` is required** and Ollama has no equivalent to carry, so it is declared per
   provider (`MaxTokens`, default 4096) and a caller's `num_predict` always wins. It is a ceiling,
   not a target; `stop_reason: "max_tokens"` comes back as Ollama's `done_reason: "length"`, which
   is visible rather than silent. Raise it if long answers arrive truncated.
4. **An unknown event type must be skipped, not fatal** — their versioning policy says new ones will
   be added. An `event: error` (an `overloaded_error`, typically) is raised mid-iteration instead,
   reusing 62 D4's contract exactly: the hub writes a terminal error chunk, the node carries a
   failed job. Without it, a request that died at token 40 returns 200 and looks finished.

The cache counts are reported and are **not** folded into `prompt_eval_count`. They are priced
differently and reported separately; a total this hub composed would match no line on the invoice.
A response with no usage block yields no counts at all rather than zeros.

## Translation choices you can see from the outside

- **Every `system` message is lifted to the top-level `system` field, in order.** Anthropic's
  `messages` has exactly two roles. **Rejected: the mid-conversation `{"role":"system"}` form** — it
  is real, and it is a **400 on Sonnet 5** while working on Opus 5 and Opus 4.8, so adopting it
  would make the translation depend on which model you happened to map.
- **`seed`, `presence_penalty` and `frequency_penalty` are dropped.** Anthropic 400s an unknown
  top-level parameter, so forwarding them refuses every request that carries one. **Rejected:
  refusing here instead** — that breaks a working client over a knob the vendor never had.
  **Rejected: approximating `seed`**, a reproducibility promise the upstream does not make, offered
  in the field a caller reads as though it did. `temperature`, `top_p`, `top_k`, `stop` and
  `num_predict` all translate.
- **`/api/generate` maps to the same endpoint** as a single user turn, because Anthropic has no
  completions API. Ollama's own `system` field lands where every other one does.
- **There are no embeddings.** `EmbedAsync` refuses with a `501` naming the reason. **Rejected: a
  503 with `Retry-After`** — "try later" for an endpoint that will never exist; **rejected: an empty
  vector**, a wrong answer shaped like a right one, in the one place nobody notices for weeks.
- **Model ids are not shape-checked**, deliberately, and it is not an inconsistency with v3.30's
  OpenRouter check. That one is a single namespace with 419-of-419 evidence. The same Anthropic
  model is `claude-opus-5` first-party, `anthropic.claude-opus-5` on Bedrock and `claude-…@2025…`
  on Vertex — and a `BaseUrl` override is exactly how somebody reaches the latter two.

## Also in this release

`AnUnknownTypeFailsStartupNamingTheTypesThatExist` used `anthropic` as its example of an unknown
type until this phase made it real. The example moved to `bedrock`; the assertion now checks all
three names. Phase 29's base64 magic-byte sniff moved to `Upstream/Base64MediaType.cs` — Anthropic's
image block wants the same answer a data URL did, and the alternative was four signatures in two
dialects, diverging the first time a fifth format arrives.

## What was **not** established, said out loud

- **No live provider was called.** Every payload in `AnthropicDialectTests` is a recorded one — a
  captured `message_start`/`message_delta`/`message_stop` stream with a `ping` and an unknown event
  in it, a captured `overloaded_error`, a captured 400 with a `request_id`. That is the track's own
  rule (D6): a test needing somebody's API key is a test CI cannot run and everyone learns to skip,
  and it bills a card on every commit. **The first real key is phase 68's**, and until then the risk
  this accepts is stated rather than mitigated: a wire-format change at the vendor is invisible to
  this suite.
- **Tool calls, thinking blocks, `cache_control`, PDFs and citations are not translated.** All are
  track-level deferrals. A `tool_use` block reaching a mesh whose job protocol is Ollama-shaped is a
  three-way mapping — a phase, not a field.
- **Nothing on the node changed.** `Backend:Type` is still `ollama|openai`; a node that speaks
  Anthropic is phase 67.
- **A provider is still only consulted when the fleet cannot serve it.** Routing a model to one by
  preference is phase 65.

## Verification

`dotnet test tests/InferHub.Tests.Shared` — **146 passed, 0 skipped** (12 of them this phase's).
`dotnet test tests/InferHub.Tests.Coordinator` — **684 passed, 43 skipped** (the Postgres/Qdrant
integration suites). `dotnet test tests/InferHub.Tests.Node` — **154 passed, 3 skipped**, run
because the exception base touches it. `ContextContractTests` green, including the budgets and the
brief's `Status:` matching `plan/00-overview.md`.

**Zero new `PackageReference`; `InferHub.Shared.csproj` is still an empty `<Project Sdk="…">`.**

## The published image, checked (the same evening)

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.31.0`, pulled ~15 minutes after the tag —
`org.opencontainers.image.version` reads **3.31.0** and `.revision` **a1abc0e**, which is this
release's commit. Run with `Providers__claude__Type=anthropic` pointed at a recording stub on the
host, and driven with `curl`. What the artifact actually did:

**On the wire, from the container:**

```
POST /messages HTTP/1.1
anthropic-version: 2023-06-01
x-api-key: sk-ant-stub-key
...
{"model":"claude-opus-5","max_tokens":4096,"messages":[{"role":"user","content":"hi"}],
 "system":"Be terse.","stream":false}
```

`x-api-key` and the version header are there, **no `Authorization` header is**, `max_tokens` was
supplied from `MaxTokens`, and the `system` turn was lifted out of `messages` — D2 and D3, observed
rather than asserted.

**Blocking**, against a stub reporting `input_tokens: 25`, `output_tokens: 7` and
`cache_read_input_tokens: 140`:

```
X-InferHub-Served-By: provider:claude
{"model":"big",…,"message":{"role":"assistant","content":"Hello from the stub."},
 "done":true,"prompt_eval_count":25,"eval_count":7,"done_reason":"stop"}
```

The 140 cache-read tokens are **not** in `prompt_eval_count`. That is D5's second half, on the
artifact.

**Streaming**, against a recorded stream carrying `output_tokens` of 1, then 8, then 15, plus a
`ping` and an event type this release has never seen:

```
{"message":{"content":"Hel"},"done":false}
{"message":{"content":"lo"},"done":false}
{"message":{"content":""},"done":true,"prompt_eval_count":25,"eval_count":15,"done_reason":"stop"}
```

**`eval_count` is 15, not 24.** The counts were taken, not summed — D5's first half, proven on the
thing that shipped rather than in a unit test. The `ping` and the unknown event produced nothing.

**A mid-stream `error` event:**

```
{"message":{"content":"Hel"},"done":false}
{"error":"the Anthropic upstream failed mid-stream: Overloaded","done":true}
```

A terminal error chunk, never a 200 that looks finished.

**Not checked, said out loud:** no live Anthropic endpoint was called here either — the stub is a
recording of their documented shapes, not their server. `/v1/models`, the `/api/generate` path and
the image-block translation were exercised by unit tests only. The node image is unchanged by this
phase and was not pulled.
