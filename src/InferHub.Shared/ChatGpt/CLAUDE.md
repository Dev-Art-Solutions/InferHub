# InferHub.Shared/ChatGpt — agent context

**Scope: `src/InferHub.Shared/ChatGpt/`, and the two hosts' halves of the same feature:
`src/InferHub.Coordinator/ChatGpt/`, `src/InferHub.Node/Backends/ChatGpt/`,
`src/InferHub.Node/LocalApi/LocalChatGptAdminEndpoints.cs` and `tools/InferHub.SignIn/`.** Sign in with
ChatGPT: OpenAI's Responses API on a ChatGPT plan, reached through a browser sign-in rather than an API
key — on the coordinator and on a node, as two separate providers that share code and nothing else.

> **Read the root `CLAUDE.md` first.** This feature is rule 4's fifth recorded exception (the
> sessions are on disk) and it holds rule 5 (no package: OAuth, PKCE, the RS256 check, AES-GCM and the
> loopback listener are all in the BCL) and rule 2 (nothing here touches ASP.NET).

## Related context

- The other four upstream dialects and the seam this one implements (61 D3): `src/InferHub.Shared/CLAUDE.md`
- The hub's providers, policies and the console this feature joins (61–66): `src/InferHub.Coordinator/CLAUDE.md`
- The node's backends and `Backend:Engines` (67, 95): `src/InferHub.Node/CLAUDE.md`, `src/InferHub.Node/Backends/CLAUDE.md`

## What the vendor's protocol is, in one place

Captured from developers.openai.com/siwc/token-sharing-open-source/* and
`https://auth.openai.com/.well-known/openid-configuration` on 2026-10-13. Every constant is in
[ChatGptOAuth](src/InferHub.Shared/ChatGpt/ChatGptOAuth.cs) and none is configurable — a setting that moves the token endpoint
moves the refresh token with it.

- **Authorize** in the system browser: `client_id=dynamic_agent_client` + `agent_name_hint=InferHub`
  on a first registration, the issued `oaiapp_…` id afterwards; `ext_agent_host_id` always; scopes
  `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`; `resource =
  https://api.openai.com/v1`; fresh `state`, `nonce`, PKCE S256 per attempt.
- **Callback** `http://127.0.0.1:<port>/auth/callback`; only the port varies; never `localhost`. A new
  registration's callback carries the issued `client_id`; without one the registration is incomplete.
- **Exchange / refresh / revoke** are form posts with the issued client id and no secret. A refresh
  omits `scope` and returns a rotated refresh token. Access tokens live an hour, refresh tokens 30 days.
- **Inference** is `POST /v1/responses` with `store: false`, `stream: true`, a Bearer access token,
  done only on `response.completed`. `/v1/models` answers `{"models":[{slug, display_name, visibility}]}`.

## Decisions recorded here

### Phase 103 (Sign in with ChatGPT — a separate provider on the coordinator and on a node) — load-bearing

**D1 — Two providers, one library, no shared state (load-bearing).** The coordinator's is a provider
`Type: chatgpt` with a session per provider under `ChatGpt:DataDirectory`; a node's is
`Backend:Type=chatgpt` (or one `chatgpt` engine) with its own session under
`<Node:DataDirectory>/chatgpt`. [ChatGptCredentialStore](src/InferHub.Shared/ChatGpt/ChatGptCredentialStore.cs) and
[ChatGptSession](src/InferHub.Shared/ChatGpt/ChatGptSession.cs) do not know which host they serve — the host picks the directory —
so "the coordinator never receives a node's tokens" is a property of where files are, not of code that
remembers. Each host has its own `ext_agent_host_id`; nothing on SignalR carries a credential, only
the model names a node's account can use, through the model report every backend makes.
**Rejected: one runtime provider the node borrows from the hub** — the plan's first rule, and a hub
holding every node's plan is a hub whose compromise spends every plan.

**D2 — A dialect of its own, beside the four, and the API-key path is untouched.**
[ChatGptResponsesClient](src/InferHub.Shared/ChatGpt/ChatGptResponsesClient.cs) is a fifth `IUpstreamDialect`. Plan usage exists
only on the Responses API, only with `store: false` + `stream: true`, and refuses `temperature`,
`top_p`, `max_output_tokens`, `user` and nine more — so `OpenAiUpstreamClient` with a different token
would 400 on the first request carrying an Ollama option. `openai-compatible` (hub) and `openai`
(node) with an API key are byte-identical to v3.67. **Rejected: an `AuthMode` flag on the existing
OpenAI type** — one type answering two wire formats depending on a credential field.

**D3 — The issued client id is the registration, and the account is the validated `sub`.** A
returning sign-in names its issued client; a callback naming another client, or an ID token whose
`sub` is another account's, replaces nothing (`identity_mismatch`). Signing out revokes, clears the
tokens and **keeps** the client id and identity, so the next sign-in is a returning one. Several
registrations live in one store with one active; "add another account" registers anew.

**D4 — Ollama options are dropped, tools are refused.** Every Ollama client sends `options`, and the
route refuses them all, so a refusal would refuse nearly everything; they are sampling hints and the
answer still answers. A request declaring `tools` relies on them, and answering without them is a 200
that looks right — so it is a 400 before anything is sent. System turns become `instructions` (the
vendor rejects a system item). A blocking call is the stream folded; a stream that ends without
`response.completed` is a failure, not a short answer.

**D5 — Signed in is not permitted, and nothing falls back to a paid API.** Plan usage is the token
response's granted `chatgpt.tokens.use.direct`, never the request's scopes and never the ID token. A
sign-in without it is stored, shown as "plan usage not granted", and every request is a **403** naming
the fix. There is no code path from a `chatgpt` provider to an API key: the hub's validator refuses an
`ApiKey` on the type, the node's refuses one on the engine, and a usage limit pauses the account
(`ChatGpt:UsageLimitPauseSeconds`) rather than routing anywhere. A different route for the request is
only ever the operator's `Policy` — 65 D3, unchanged.

**D6 — A remote coordinator signs in through a helper and a one-time pairing (load-bearing).** A
`127.0.0.1` callback reaches the machine with the browser, which is not the server. The console creates
a pairing (admin key; code hashed at rest, ten minutes, one use, bound to one provider);
`inferhub-signin` — or `InferHub.Node chatgpt pair` — on the administrator's machine asks the hub
what to sign in as, runs the browser sign-in **with the coordinator's host id and a nonce the
coordinator chose**, exchanges the code locally (the vendor's self-hosted-VM procedure), and posts the
token response once, in a body, over HTTPS (plain HTTP only to a loopback hub). The hub verifies the ID
token itself against its own nonce, so a token from any other sign-in does not verify there. The
pairing routes are `/chatgpt-pairing/*`, outside both key middlewares, because the code is their whole
authentication; an unknown, expired and spent code get one sentence. **Rejected: the hub exchanging the
code** — the laptop would never hold a refresh token, but the plan asks for local completion and an
authorization code in flight is worth exactly what the tokens are. **Rejected: a callback on the hub** —
a public redirect URI is not what this client type is registered for.

**D7 — Tokens never reach a URL, a page, a log or a status payload.** The authorize URL can carry an
ID-token hint, so it is never logged, and the solo page's flow sends only the login hint. The console
reads state from `/api/admin/chatgpt/{id}`; `/api/status` (readable with an inference key) carries
signed-in / plan-usage / paused and **not the email**. The OAuth `HttpClient`s have no loggers. At rest
`accounts.bin` is AES-GCM under `chatgpt.key` beside it, written temp-then-rename, owner-only on Unix —
a copied store file alone is ciphertext; an attacker who can read the directory is not stopped, and the
notes say so.

**D8 — `@selected` lets the console pick the model without a config edit.** A `ModelMap` value of
`@selected` routes to the model chosen for the active account in the console — checked against that
account's own catalogue — and is a **409** naming the panel until one is. The map key is still the
consent (22 D5). The validator refuses `@selected` on any other type.

**D9 — Status codes survive for this type only.** `InferenceCore` answers a ChatGPT failure with its
own status (429 usage limit, 403 permission or region, 401 sign in again, 409 no model) instead of
the 502 every provider failure has been since phase 22; the four key-authenticated types keep their
502, so nothing that worked yesterday answers differently.

**D10 — A node signs in at its own console; a service never opens a browser.** `chatgpt login` on
`InferHub.Node` or `InferHub.Node.Service.exe` runs as the user, opens their browser on a loopback
callback and writes the node's store; the running node re-reads the file when its write time changes,
so no restart. A solo node also has `/api/admin/chatgpt`, where the node starts the listener and the
page opens the URL — useful only when the browser is on that machine, and the page says so.

Tests: `ChatGptSignInTests`, `ChatGptDialectTests` (Shared — a real loopback listener, really signed
RS256 ID tokens, the encrypted store), `ChatGptProviderTests` (Coordinator — a real Kestrel hub, the
pairing, inference through `/api/chat`), `ChatGptBackendTests` (Node). `FakeOpenAi` in `Tests.Common`
is OpenAI's auth server and Responses API as one handler. **No test calls OpenAI**, and no real
account was used: what that leaves unestablished is in `.claude/release-notes-v3.68.0.md`.
