# InferHub v3.70.0 — provider limits on every host, and video from Google

Two phases in one release.

- **Phase 106 — provider limits.** The coordinator and every node show how close each of their cloud providers is to a
  limit: a ChatGPT plan's **five-hour** and **weekly** windows, an API key's rate limits, this month's spend against a
  budget, and the vendor's last refusal. Refreshed on every response and every `Quota:RefreshSeconds`.
- **Phase 105 — video from Google.** `/v1/videos` and `/api/videos/*` work on a box with no GPU, from **Veo 3.1** or
  **Gemini Omni**, through the same job model, progress events and read-once `/content` a GPU clip uses.

## Provider limits (phase 106)

The console has a new **Provider limits** panel, and `GET /api/admin/quota` (admin key) returns the same data. Each row
is one provider on one host — **the coordinator's own and each node's own, side by side and never added up**, because
each holds its own credential and spends its own limit.

| What | Where it comes from |
|---|---|
| ChatGPT sign-in: % of the five-hour and the weekly window, and when each resets | `x-codex-primary-*` / `x-codex-secondary-*` headers on a real response, then OpenAI's Codex usage endpoint (`Quota:ChatGptUsageUrl`) |
| OpenAI key: requests/min and tokens/min used | `x-ratelimit-*` on every response |
| Anthropic key: requests, tokens, input and output tokens used | `anthropic-ratelimit-*` on every response |
| This month's spend against `Billing:MonthlyBudgetUsd` | OpenAI `/v1/organization/costs` and Anthropic `/v1/organizations/cost_report` with `Billing:AdminApiKey`; OpenRouter `/key` with the ordinary key |
| The vendor's last refusal | any 429 or 402, in the vendor's own words, until the next success |

```jsonc
"Providers": { "openai": { "Billing": { "MonthlyBudgetUsd": 50, "WarnPercent": 80 } } }
// env: Providers__openai__Billing__AdminApiKey=sk-admin-...
"Quota": { "RefreshSeconds": 300, "WarnPercent": 80, "ChatGptUsageUrl": "https://chatgpt.com/backend-api/wham/usage" }
```

A node has the same under `Upstream:Billing` and `Backend:Engines:<name>:Billing`, and reports its rows to the hub
(`ReportQuotaState`, at most every ten seconds). An `openai` backend pointing at a local vLLM is left out unless you give
it a budget. **Nothing is estimated and nothing is enforced**: a percentage is one a vendor stated or its spend over your
budget, and a budget reached turns the row red — the vendor's own cap is the one that refuses. The panel is admin-only;
`/api/status`, which an inference key can read, carries none of it.

## Video from Google (phase 105)

```jsonc
"Providers": { "google": { "Type": "gemini",
  "Media": { "video": { "veo": "veo-3.1-generate-preview", "omni": "gemini-omni-1.1-flash" } } } }
```

- **Veo** is submitted, polled every ten seconds and fetched. It renders **4, 6 or 8 seconds**, and 1080p only at 8.
- **Gemini Omni** answers in one call and chooses its own length; `seconds` becomes a warning and the real length is
  read from the MP4 header.
- `size`: `1280x720`, `720x1280`, `1920x1080`, `1080x1920` (default `1280x720`). `1920x1080` is now admitted at the edge
  although 1080 is off the 16-pixel grid; a GPU recipe that does not offer it still refuses it naming its list.
- A duration or size the vendor does not render, or a `negative_prompt`, fails the job with a 400 sentence **before
  anything is sent** — on the async routes the same way a GPU recipe's refusal does.
- **The key is sent only to Google's own host.** Google's download redirects to storage, and .NET drops only
  `Authorization` on a redirect — not `x-goog-api-key`. So both hosts now build the media client **with automatic
  redirects off** and follow the redirect by hand, without the key. A URI on any other host is refused.
- A clip is never submitted twice. A cancel stops polling; Veo documents no cancel, so the vendor may still bill it.
  A video call gets at least 600 s whatever `TimeoutSeconds` says.
- Metered as `video_seconds` and one megapixel-step per megapixel per second.
- **No OpenAI video.** OpenAI shut Sora 2 and its Videos API down on 2026-09-24; `video` on an OpenAI provider fails
  startup and says so.

## Found by running it

The live run from source — a real coordinator and a real meshed node, configured through the environment against a
stand-in OpenAI + Gemini — showed the panel and the API agreeing: the hub's `openai` at 92% of its request window and
$12.50 of $20; the node's own `openai` at $12.50 of its $10 budget (`limit`); a 429 `insufficient_quota` shown in the
vendor's words and cleared by the next success; a Veo clip at 9:16 / 720p / 6 s submitted once, polled twice, fetched
with `x-goog-api-key` from the vendor host and **without** it from the redirect target.

The stand-in itself needed one fix: InferHub's upstream clients send **chunked** request bodies, and Python's
`http.server` reads none without `Content-Length` — the 429 test was answering an empty body until it learned to
read chunks. (Known from v3.62; recorded again because it cost a run.)

## The published binaries

The published `inferhub-coordinator:3.70.0` and `inferhub-node:3.70.0` images (revision label `1e4843a`, the tag commit) were pulled from GHCR and their `/app` binaries run natively against the same stand-in: a hub provider chat and a node chat both read their rate windows (92% of requests), spend showed on both hosts (hub 62.5% of $20, node 125% of $10, `limit`), and a **1920x1080** Veo clip was submitted at 16:9 / 1080p / 8 s, polled, and fetched with the key from the vendor host and without it from the redirect target. This exercised the binaries, not the containers' OS layer.

## Not established

- **No real ChatGPT account's windows were read.** Sign in with ChatGPT documents no usage endpoint and no usage
  headers. The `x-codex-*` headers are what OpenAI's Codex client reads on its own backend; whether OpenAI sends them on
  a Sign in with ChatGPT response at `api.openai.com` is unknown. The Codex usage endpoint is undocumented, may refuse a
  Sign in with ChatGPT token (a different audience) or be behind Cloudflare's browser check; when it refuses, the panel
  says so and asks again in an hour. If neither source answers for a real account, the panel shows only the 429 when the
  plan runs out — which is what v3.68 already showed.
- **No real vendor was called** — no OpenAI or Anthropic admin key, no OpenRouter key, no Veo or Omni render. Every
  vendor shape here is from the vendor's documentation read on 2026-10-15, exercised against stand-ins.
- Gemini reports no spend to an API key; a Google project's budget shows only as the 429 it causes.
- Prometheus gains no series for limits this release.

## Tests

`QuotaTests`, `CloudVideoTests` (Shared), `QuotaProviderTests` and three video tests in `CloudMediaProviderTests`
(Coordinator — a real hub, the real dispatcher and job registry, no node), `QuotaNodeTests`, `CloudVideoNodeTests`
(Node — a solo node renders a clip from its own key), `QuotaMeshTests` (Mesh — a node's limits over real SignalR, its key
in no payload). Zero new `PackageReference`; `InferHub.Shared.csproj` is still empty.
