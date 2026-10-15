# InferHub.Shared/Quota — agent context

**Scope: `src/InferHub.Shared/Quota/`, and the two hosts' halves of the same feature:
`src/InferHub.Coordinator/Services/CoordinatorQuota.cs`, `src/InferHub.Coordinator/Endpoints/QuotaEndpoints.cs`, the
console's Provider limits panel, `src/InferHub.Node/Quota/NodeQuotaService.cs` and `ReportQuotaState` in
`CoordinatorConnection`.** How close each cloud provider is to a limit — a ChatGPT plan's five-hour and weekly windows,
an API key's rate limits, the month's spend against a budget, and the vendor's last refusal — on the coordinator and
on every node, each for its own credentials.

> **Read the root `CLAUDE.md` first.** This feature holds rule 4 (all of it is in memory; a restart costs a dash on a
> page until the next response or refresh), rule 5 (no package: headers, JSON, `HttpClient`), rule 2 (nothing here
> touches ASP.NET) and rule 7 (nothing here reads a body except a 429/402 refusal's error object).

## Related context

- The provider types and their dialects (61–64): `src/InferHub.Coordinator/CLAUDE.md`, `src/InferHub.Shared/CLAUDE.md`
- The ChatGPT session whose windows are shown, and why it is never a paid API (103 D5): `src/InferHub.Shared/ChatGpt/CLAUDE.md`
- The node's backends and engines whose limits it reports (67, 95): `src/InferHub.Node/CLAUDE.md`

## What the vendors expose, in one place

Read 2026-10-15. **Sign in with ChatGPT** documents no usage endpoint and no usage headers — a usage limit is a 429
`subscription_sharing_usage_limit_exceeded`, which the vendor says not to read as "the plan is empty". OpenAI's own
Codex client reads `x-codex-primary-*` / `x-codex-secondary-*` (used percent, window minutes, reset) on responses and
`GET https://chatgpt.com/backend-api/wham/usage` (`plan_type`, `rate_limit.primary_window` / `secondary_window`) —
both undocumented. **OpenAI with a key**: `x-ratelimit-*` per response; spend only through
`GET /v1/organization/costs` with an **admin** key (`amount.value` in dollars). **Anthropic**: `anthropic-ratelimit-*`;
spend through `GET /v1/organizations/cost_report` with an admin key (`amount` is **cents**, a decimal string); the
spend-limit read API is Claude Enterprise only. **OpenRouter**: `GET /api/v1/key` tells any key its own spend and limit.
**Gemini**: nothing — a Cloud Billing cap shows only as the 429 it causes.

## Decisions recorded here

### Phase 106 (provider limits on the coordinator and on every node) — load-bearing

**D1 — Headers are read by a handler in the pooled client, and the dispatch says whose they are (load-bearing).**
[QuotaObservingHandler](src/InferHub.Shared/Quota/QuotaTracker.cs) sits in the hub's `inferhub-provider` and
`inferhub-cloud-media` clients and the node's `openai-upstream` and `chatgpt-responses` clients; `QuotaScope.Enter`
(an `AsyncLocal`) is set by the one place that knows the provider — `ProviderDispatcher`, `MediaProviders`,
`UpstreamBackend`, `ChatGptBackend`. No dialect learned a parameter, and a call with no scope records nothing.
**Rejected: one `HttpClient` per provider with its own handler** — it unpicks the factory's pooling for a label.
**Rejected: parsing headers inside each dialect** — five copies of one reading.

**D2 — Only what a vendor said is shown; nothing is estimated (load-bearing).** Spend comes from a vendor API or is
absent with a sentence saying why; a percentage is a window the vendor stated or spend over the operator's
`Billing:MonthlyBudgetUsd`. **Rejected: pricing tokens this host counted** — a second invoice in config that drifts from
the real one, and confidently wrong is worse than a dash (28 D5's argument about manufactured zeros).

**D3 — The ChatGPT windows come from the headers first and the Codex usage endpoint second, and the endpoint is
optional.** `Quota:ChatGptUsageUrl` (default `https://chatgpt.com/backend-api/wham/usage`, empty turns it off) is
asked with the host's own access token and the `chatgpt_account_id` from its own stored ID token. A refusal is a note
on the panel and an hour's back-off, never an error. The token goes to OpenAI's host and nowhere else; it is the same
credential 103 D7 already keeps out of logs and pages.

**D4 — Each host reports its own providers; the hub adds nothing up.** The node sends `NodeQuotaState` on
registration, on the model loop and (at most every ten seconds) on change — the 44 D6 mailbox. A node lists its
backend when it is a vendor, an `openai` backend or engine only when it is `api.openai.com` or has a budget (a vLLM has
no limit to show), and its ChatGPT sign-in once. **Rejected: summing across hosts** — two nodes on two keys are two
limits, and one number would describe neither.

**D5 — Admin-only, beside `/api/status` rather than in it.** `GET /api/admin/quota` (and `POST …/refresh`, which asks
the hub's vendors now) carries spend and budgets; `/api/status` is readable with an inference key and does not.
`Billing:AdminApiKey` is environment-only like every key, sent only to its own vendor's cost endpoint, never logged
(the quota `HttpClient`s have no loggers) and never in a payload.

**D6 — Shown, never enforced.** A budget reached turns the row red; nothing routes on it. The vendor's own cap is the
one that refuses, and a hub that refused first on an estimate would be D2's mistake with consequences.

Tests: `QuotaTests` (Shared — headers, levels, probes, the handler), `QuotaProviderTests` (Coordinator — a real hub,
the real dispatcher and client, `/api/admin/quota`), `QuotaNodeTests` (Node — which backends are listed),
`QuotaMeshTests` (Mesh — a node's report over real SignalR). **No test calls a vendor**; that no real ChatGPT
account's windows were read is said in the release notes.
