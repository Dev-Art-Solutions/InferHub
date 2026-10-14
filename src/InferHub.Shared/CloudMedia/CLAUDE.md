# InferHub.Shared/CloudMedia — agent context

**Scope: `src/InferHub.Shared/CloudMedia/`, and the two hosts' halves of the same feature:
`src/InferHub.Coordinator/Services/MediaProviders.cs` (with the provider `Media` map in `ProviderOptions.cs`) and
`src/InferHub.Node/Tools/CloudMediaRuntime.cs` (with `Configuration/CloudMediaOptions.cs`).** Pictures, voices and
transcripts from OpenAI and Google with an API key, on the coordinator and on a node, through the routes a GPU worker
already answers.

> **Read the root `CLAUDE.md` first.** This feature holds rule 5 (no package: `HttpClient`, multipart, JSON), rule 2
> (nothing here touches ASP.NET) and rule 7 (nothing here logs; the hosts log capability, model, provider, outcome).

## Related context

- The renderers and request shapes this client answers into (42, 46, 50): `src/InferHub.Shared/CLAUDE.md`
- The hub's providers, `Policy` and the console (61–66): `src/InferHub.Coordinator/CLAUDE.md`
- The tool runtime the node's half sits beside (41, 48): `src/InferHub.Node/Tools/CLAUDE.md`
- Why a ChatGPT sign-in is not one of these (103 D5): `src/InferHub.Shared/ChatGpt/CLAUDE.md`

## What the vendors allow, in one place

Read 2026-10-14. **Sign in with ChatGPT** (developers.openai.com/siwc/token-sharing-open-source/preview-limitations):
Responses API only; image generation, audio/video input and the transcription API are **unsupported**; image input is
supported — which v3.68 already sends as `input_image`. **OpenAI with a key**: `/images/generations|edits|variations`
(gpt-image answers base64 and refuses `response_format`; dall-e answers a URL unless asked), `/audio/speech`,
`/audio/transcriptions` (`verbose_json` with segments for whisper-1 only). **Gemini API with a key**: Imagen through
`:predict` (`sampleCount`, five aspect ratios), image models and TTS models through `:generateContent` with
`responseModalities` (TTS answers `audio/L16;codec=pcm;rate=24000`), 20 MB per inline request.

## Decisions recorded here

### Phase 104 (pictures, speech and transcripts from OpenAI and Google with an API key) — load-bearing

**D1 — One client, `ToolJob` in and `ToolResult` out, used by both hosts (load-bearing).**
[CloudMediaClient](src/InferHub.Shared/CloudMedia/CloudMediaClient.cs) answers in a worker's shape — PNG attachments
plus `{steps, images:[{width,height}], warnings}`, one audio attachment, a `{text, language, duration, segments}`
transcript — so `ImageRenderer`, `AudioRenderer`, the job store and metering did not change, and a cloud picture
cannot disagree with a GPU picture about an envelope. One `HttpClient` with no base address and no default headers:
the route carries the URL and the key per request, so no header set for one vendor reaches another. **Rejected: a
provider arm inside each renderer** — two readings of one answer, the 42/46 parity bug. **Rejected: a Python worker
that calls the vendor** — a process and a venv to make an HTTPS call.

**D2 — `Media: { capability: { local: upstream } }`, and the key is the consent (22 D5).** Claimed per (capability,
model) by exactly one enabled provider; a second claim fails startup naming both (61 D1), checked by
`CloudMediaCatalog.Validate` on both hosts. A separate map from `ModelMap`, so one name never means a chat model and
a picture model at once and `/api/tags` never lists a voice as `chat`; `/v1/models` lists a cloud-only name with
the kinds it claims (65 D5).

**D3 — Two dialects: OpenAI's (`openai-compatible` on the hub, `openai` on a node) and Gemini's.** `Media` on
`anthropic`, `openrouter` or `chatgpt` fails startup — on `chatgpt` because 103 D5 forbids any path from a sign-in to a
paid API, and because the vendor refuses the work anyway. An `imagen-*` id goes to `:predict`, anything else to
`:generateContent`; the model is a path (`models/x` unless it already contains a slash, 64 D2's normalisation).

**D4 — What a vendor cannot honour is refused before the call (91).** A negative prompt, `seam_repair`/`reproject`,
a mask to Gemini, a `luminance` mask to OpenAI (50 D2: inverting one decodes pixels, 46 D6), Gemini variation, an
Imagen edit, a Gemini voice in anything but `wav`/`pcm` (raw PCM; an mp3 encoder in C# is a dependency) or with a
speed, a size that is no Gemini aspect ratio, and `stream_format`: each a 400 naming what works, and **no request
leaves the host**. Steps, guidance, seed and strength shape *how*, so they are answered without and listed in the
envelope's existing `warnings`. Sizes to OpenAI are passed through and its 400 relayed (62 D5). A size sent to Gemini
as a ratio says so in `warnings`, and the image's real size is read off its PNG header (`MediaBytes`, header only).

**D5 — A cloud picture meters one step per megapixel.** Megapixel-steps measure card work and a cloud picture spends
none, but zero would make every image quota free the day a key is set. A voice meters characters (42 D7); a transcript
the vendor's `duration`/`usage.seconds`, else a WAV header's length, **else zero** — never estimated from bytes.

**D6 — Vendor statuses become the `ToolResult` kinds the edges already render.** 4xx → `Refused(invalid_request)` with
the vendor's own message (400); 429 → `Retry` with its `Retry-After` (503); 401/403 → `Failed` naming the provider's
key (502 — the caller cannot fix the operator's key); 5xx, timeout, unreachable → `Failed` (502). The key is in no
message, no log line (both hosts' `HttpClient`s have `RemoveAllLoggers`) and no status payload (`credential:
configured|absent`). The caller's upload filename never reaches the vendor: the multipart name is `audio.<ext>` from
the bytes' magic (42 D5).

**D7 — The hub chooses node-or-provider per (capability, model) with the provider's `Policy`, and never retries.**
`MediaProviders.Choose`: `no-node` (default) and `no-node-or-saturated` serve from the vendor only when the router found
no node — and for image jobs only when **no node declares the pair at all**, so a busy fleet is waited for rather
than quietly billed; `prefer`/`only` go to the vendor first, and `only` never asks the router. **A failed vendor call
is not retried on a node** (unlike 65 D3's chat backstop): the vendor may already have charged — 56's rule against
spending one request twice. A cloud image job runs `running` on `provider:<id>`, holds no node slot, reports no
progress; `X-InferHub-Served-By: provider:<id>` on every route. A streamed upload (over `Tools:MaxAttachmentBytes`)
cannot go to a vendor and is a 413 when only a provider could serve it.

**D8 — A node's cloud media is its own `CloudMedia` section, and never the hub's keys.** `ToolExecutor` asks
`CloudMediaRuntime` after the scorer and the backend's own jobs and before the worker pool (94 D1's order);
`ToolExecutor.ToolCapabilities` folds its capabilities beside the pool's, and both the hub report and solo status read
that one list, so `Node:Capabilities:Disabled` and a profile's disabled models narrow a cloud name like any tool. A
streamed upload is read into memory up to the node's own ceiling. **Rejected: the hub pushing keys to nodes** — 103
D1's argument, for API keys.

Tests: `CloudMediaClientTests` (Shared — each dialect and capability through the real renderers), `CloudMediaProviderTests`
(Coordinator — real Kestrel, no node, the real job registry; privacy at Trace), `CloudMediaNodeTests` (Node — a solo node
through its composition root), `ChatGptProviderTests.APictureInAnOpenAiChatReachesTheResponsesApiAsAnInputImage`.
`FakeMediaVendor` in `Tests.Common` is both vendors as one handler. **No test calls OpenAI or Google**; what that leaves
unestablished is in the release notes.
