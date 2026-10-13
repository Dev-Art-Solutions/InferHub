# InferHub v3.68.0 — Sign in with ChatGPT: your plan, on the coordinator and on a node

Until now the only way InferHub reached OpenAI was an API key: `Type: openai-compatible` on the hub,
`Backend:Type=openai` on a node. OpenAI's *Sign in with ChatGPT* for open-source, locally hosted apps lets
a user run the Responses API on their **ChatGPT plan** after a browser sign-in, with no key at all. This
release adds that as a **new type in two places**, and the two places are separate: the coordinator signs
in as one account and serves it as a cloud provider; a node signs in as its own account and serves it as a
backend. Neither ever sees the other's tokens. The key-based types are unchanged.

## What's new

**On the coordinator — a provider of `Type: chatgpt`:**

```jsonc
"Providers": {
  "plan": {
    "Type": "chatgpt",
    "Policy": "only",
    "ModelMap": { "chatgpt": "@selected", "sol": "gpt-5.5" }
  }
}
```

The map is still the consent. A value is the account's model slug (as its own `/v1/models` lists it), or
`@selected` — the model you pick for the account in the console (a `409` naming the panel until you do).

**Signing in a remote coordinator.** A sign-in returns to `http://127.0.0.1:<port>/auth/callback`, and
`127.0.0.1` is the computer with the browser — not the server. So the console (**Cloud providers → ChatGPT
(Coordinator) → Continue with ChatGPT**) creates a **one-time pairing code**, and you run the helper where
your browser is:

```text
inferhub-signin --hub https://hub.example.com
Pairing code from the coordinator's console: ihp_…
Waiting for the browser on http://127.0.0.1:1234/auth/callback …
```

The helper asks the hub what to sign in as, opens your browser with **the coordinator's** host id and a
**nonce the coordinator chose**, exchanges the code itself, and hands the token response over once, in a
request body, over HTTPS (plain HTTP only to a loopback hub). The coordinator checks the ID token itself —
signature against OpenAI's JWKS, issuer, audience, expiry and its own nonce — so a token from any other
sign-in does not verify there. The code is hashed at rest, works once, and expires in ten minutes; an
unknown, expired and used code get the same sentence. `inferhub-signin` is attached to this release for
Windows, Linux and macOS (self-contained, ~37 MB); `InferHub.Node chatgpt pair --hub …` does the same.

**On a node — `Backend:Type=chatgpt`, or one engine beside the others:**

```jsonc
"Backend": { "Engines": { "ollama": { "Type": "ollama" }, "plan": { "Type": "chatgpt" } } }
```

Sign it in at the node: `InferHub.Node chatgpt login` (or `InferHub.Node.Service.exe chatgpt login` for the
Windows service, run as the user whose browser should open — a service never opens one). The running node
picks the new session up without a restart, reports the account's models to the coordinator **by name** and
runs them in solo mode or when the coordinator routes a job here. `chatgpt status | logout | use --account
<id>`; a solo node also has a page at `/api/admin/chatgpt`.

**Port 1234 is preferred, and only the port ever varies.** The listener binds `127.0.0.1` only (never
`0.0.0.0`, never `localhost`); if 1234 is taken it takes a free port and uses it consistently in the
authorization and the exchange.

**What it will and will not do:**

- **Signed in is not allowed.** Plan usage is the `chatgpt.tokens.use.direct` permission *granted* at
  sign-in. Without it the account shows as signed in, and every request is a **403** naming the fix.
- **Nothing ever falls back to a paid API.** A `chatgpt` provider or engine with an `ApiKey` fails startup.
  A usage limit is a **429** and pauses that account for `ChatGpt:UsageLimitPauseSeconds` (300).
- **The status is the vendor's, for this type only.** 429 usage limit, 403 permission or region, 401 sign in
  again — on the hub, through a node, and on a solo node's Ollama and OpenAI surfaces. The four
  key-authenticated provider types keep the 502 they have always had.
- **Ollama options are dropped, tools are refused.** The route refuses `temperature`, `top_p`,
  `num_predict` and the rest, so they are not sent; a request declaring tools is a 400 before anything is
  sent. A system turn becomes `instructions`. Every request streams upstream (`store: false`,
  `stream: true`) and counts as done only on `response.completed`. Chat only — no embeddings.
- **Several accounts.** Each registration keeps its own issued client id; "Add another account" registers
  anew, a returning sign-in that comes back as a different account replaces nothing, and signing out
  revokes the session but keeps the registration so the next sign-in is a returning one.
- **No token leaves where it belongs.** Sessions are encrypted at rest (AES-GCM, key file beside them,
  written temp-then-rename, owner-only on Unix) under `ChatGpt:DataDirectory` on the hub (`/data/chatgpt`
  in the image) and `<Node:DataDirectory>/chatgpt` on a node. No token is in a URL, the console,
  `/api/status` (which also does not carry the account's email — an inference key can read it) or a log.
  Refresh is serialized, and the rotated refresh token is saved in the same write as the access token.

**Rule 4's fifth recorded exception**, and **zero new `PackageReference`**: OAuth, PKCE, the RS256 ID-token
check, AES-GCM and the loopback listener are all in the BCL. `InferHub.Shared.csproj` is still empty.

## Found by running it

- **The console's ChatGPT panel asked for the admin key with `prompt()` the moment it loaded** — a pop-up on
  every page load for any hub with a `chatgpt` provider, and it crashed a headless browser outright (that
  is how it was found). A read the page makes on its own now never prompts; the panel says it needs the key.
- **The Cloud providers table said `configured` for a signed-out ChatGPT provider** — it only knew
  `absent`/`configured`. It now shows `signed-out`, `no-plan-usage` or `signed in`.
- **A solo node answered a signed-out ChatGPT request with a 502** on the Ollama surface, and through the
  executor on every surface. A ChatGPT refusal now keeps its 4xx through the node, like a phase-100
  catalogue refusal, and the solo Ollama chat route uses the result's status as the hub's already did.

## Checked against OpenAI's real endpoints (no account)

With the release's own code, against `auth.openai.com` and `api.openai.com`: the token endpoint answers a
bogus code and a bogus refresh token with `400 invalid_grant`, which is read as an unusable refresh (the
session is cleared and the registration kept); OpenAI's JWKS is the `{kty, kid, n, e}` shape the validator
reads; `/v1/responses` and `/v1/models` with a bad bearer answer 401 with the `{"error": {message, code}}`
envelope, read with its `x-request-id`. The authorize URL itself sits behind a Cloudflare browser
challenge, so it could not be fetched from a script.

## What was not established, said out loud

- **No real ChatGPT account was signed in.** The consent screen, the issued `oaiapp_…` client id coming back
  on the callback, a real ID token's claims, granted scopes and a real `/v1/responses` stream on a plan were
  all exercised against `FakeOpenAi` — shapes taken from OpenAI's documentation on 2026-10-13 — and never
  against the vendor. The first real sign-in is the user's.
- **Whether OpenAI accepts this app.** Plan usage through an open-source, self-hosted app — and especially
  from a remote server — is OpenAI's to grant; their documentation says remote and hosted use may need
  approval. The sign-in shows what was granted.
- **The real usage-limit event** (`subscription_sharing_usage_limit_exceeded` mid-stream) was only ever
  scripted.
- **`text.format`** (Ollama's `format: "json"` / a schema) is sent as the Responses API's own field; whether
  plan usage accepts it was not checked.
- **At-rest encryption is a key file beside the data.** A copied `accounts.bin` alone is ciphertext; anyone
  who can read the directory can read both.

## Upgrading

Nothing changes unless you add a `Type: chatgpt` provider or a `chatgpt` backend/engine. A hub with no such
provider creates nothing on disk and answers every route as v3.67 did.

Tests: `ChatGptSignInTests`, `ChatGptDialectTests` (Shared — a real loopback listener, really-signed RS256 ID
tokens, the encrypted store), `ChatGptProviderTests` (Coordinator — a real Kestrel hub: pairing, inference
through `/api/chat`, 401/409/429), `ChatGptBackendTests` (Node). Whole suite: **2147 passed, 63 skipped**.
