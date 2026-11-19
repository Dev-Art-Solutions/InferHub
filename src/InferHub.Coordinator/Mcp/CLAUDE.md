# Mcp/ and Users/ — agent context

**Scope: `src/InferHub.Coordinator/Mcp/` and `src/InferHub.Coordinator/Users/`.** People with roles on a hub, and the hub as an
MCP server that an assistant (Claude Code, claude.ai, ChatGPT, the Claude and OpenAI APIs) can attach to and sign in to.

> **Read the root `CLAUDE.md` first** — its auth table and rule 4 are what this area extends.

## Where things are

| File | Holds |
|---|---|
| `Users/UserOptions.cs` | `Users:*`, the five permissions, the four built-in roles, the validator (names, custom roles) |
| `Users/UserDirectory.cs` | the users, their hashed keys, `users.json`; `Resolve(token)`, `Authenticate(user, key)` |
| `Users/UserEndpoints.cs` | `/api/admin/users[/{id}[/keys[/{keyId}]\|/sessions]]`, `/api/admin/roles` |
| `Auth/AdminApiKeyMiddleware.cs`, `Auth/BearerApiKeyMiddleware.cs` | where a user key is read and its role enforced (`WhyRefused`, `IsDiscovery`) |
| `Mcp/McpEndpoints.cs` | `/mcp` — the transport, sessions (`McpSessions`), Origin check, the 401 that points at sign-in |
| `Mcp/McpCaller.cs` | which credential is calling `/mcp` and with which permissions |
| `Mcp/McpTools.cs` | `list_models`, `chat`, `embed`, `fleet_status` |
| `Mcp/OAuthStore.cs`, `Mcp/OAuthEndpoints.cs` | the authorization server: metadata, registration, the sign-in page, tokens, revocation |
| `wwwroot/users.html` + `users.js` | the page for all of it, with the four connection snippets |

## Decisions recorded here

### Phase 154 (people with roles, and the coordinator as an MCP server any assistant can sign in to)

User-requested: make the coordinator more flexible — MCP done properly so it attaches to Claude or OpenAI, roles, many users made,
access to every available model.

**D1 — `/mcp` is MCP's Streamable HTTP transport, and every answer is one `application/json` body.** The spec lets a server answer a
POST with JSON or with an SSE stream, per request; nothing here has progress worth streaming (`chat` returns one message, like a
non-streamed `/api/chat`), and a JSON answer is what every client handles first. A notification or a client's response is `202`
with no body; `GET` is `405` (this server never speaks first, so there is no stream to open); `DELETE` ends a session. Protocol
versions 2025-11-25, 2025-06-18 and 2025-03-26: `initialize` answers the client's version when it is one of them, else the newest; a
later request naming another in `MCP-Protocol-Version` is a `400`. A session id is issued on `initialize` **and bound to the
caller who opened it** — presented by anyone else, unknown, or idle a day, it is a `404`, the spec's "initialize again". A request
with no session id is served (stateless clients exist, and every request carries its own credential). A batch (2025-03-26 only) is
answered rather than refused. An `Origin` header that is not this hub's own, `Mcp:PublicUrl`'s or in `Mcp:AllowedOrigins` is a
`403` (the spec's DNS-rebinding defence); server-side clients send none and are not affected. **Considered and rejected: the
official C# MCP SDK** — a `PackageReference` (rule 5) for a JSON-RPC envelope of five methods; the Qdrant precedent (33 D2) again.

**D2, load-bearing — a role is a name over five permissions, and a user's key goes exactly as far as its role, in the two guards
that already exist.** `models` (the catalogue: GET `/api/tags`, `/v1/models`, `/api/status`, `/api/version`), `inference` (everything
else under `/api` and `/v1`), `admin.read` (GET/HEAD `/api/admin/*`, `/metrics`), `admin.write` (the rest of the admin API) and
`users` (`/api/admin/users`, `/api/admin/roles` — people are their own permission, so an operator who can cordon nodes cannot
make themselves an admin). Built in: viewer = models; user = + inference; operator = + admin.read; admin = all five.
`Users:Roles` adds roles; **a built-in cannot be redefined** (the validator stops the hub), because "admin" meaning something
different on two hubs is the kind of difference an operator learns from an incident. A user key that resolves runs as a
`ResolvedClient` with the user's limits, collections, priority and organisation, so admission (25), the scope (130) and the audit
line (125, `who = user:<id>`) treat it exactly as an `Auth:Clients` entry. Beyond the role: `403` with the role's name, never `401`
(the key is good; the request is not allowed). A disabled user, a removed key, a deleted user, or a role a config change removed:
the key resolves to nobody. **An admin user may run models**, unlike an admin key (root auth table): a role describes a person, and
the person who runs the hub also uses it; the admin key stays a machine credential. **Rejected: a third middleware** — every surface
would then pass three guards whose order matters, which is how 130 found its audit line missing an organisation.

**D3 — issued keys are 256 random bits, kept as SHA-256, and found with a dictionary lookup.** The root rule (fixed-time compare,
every entry) exists because a configured key may be a short human string; an issued key never is, and a dictionary lookup's
timing tells a caller only about the hash of what they sent — which they can compute themselves. **Rejected: the loop** — linear in
users × keys on every request, for no secret it protects. The key is in exactly one response, the one that made it; the list shows
its last four characters. `users.json` holds the hash; a hub restart loses nothing. **Rule 4's eleventh exception**, under
`Users:Persistence=file` (the default once `Users:Enabled`, because a person whose account evaporates on restart was never given
one): `users.json` and `oauth.json` under `Users:DataDirectory` (the image: `/data/users`), written whole and moved over; a file that
does not parse stops the hub (152 D2's reasoning — here starting empty would also lock everyone out). Losing it costs re-issuing
keys, never a request let in. Each hub of a cluster keeps its own — named, not solved.

**D4 — every credential the hub accepts works on `/mcp`, with the permissions it has everywhere else.** A user key or an OAuth access
token: the user's role. An `Auth:Clients`/`ApiKeys` key: models + inference. An admin key: models, admin.read, admin.write, users —
**not inference**, so an admin key still never runs a model, on MCP either. No token from loopback while
`RequireAuthForLoopback=false`: everything, as on `/api`. `tools/list` shows only what the caller holds, and calling a tool the
role lacks is the same `-32602 unknown tool` as one that does not exist — a tool is not advertised as forbidden. An access token
counts only for the resource it was issued for (RFC 8707): a token minted for `https://hub/mcp` is refused if the hub is reached
under another address, and it is refused on `/api` and `/v1` (it was granted for MCP).

**D5 — the hub is its own OAuth 2.1 authorization server, and the person signs in with one of their keys.** claude.ai and ChatGPT
connectors attach to a remote MCP server only through OAuth: the 401 names `/.well-known/oauth-protected-resource/mcp` (RFC 9728),
that names the hub as issuer (RFC 8414 metadata), the client registers itself (RFC 7591, open — registering grants nothing;
`Mcp:OAuth:MaxClients` bounds it), and the code flow runs with **PKCE S256 only**, `resource` checked, `iss` on the redirect
(RFC 9207). The authorize page asks for the user's `ihu_…` key; `DENY` framing, no cache, no referrer. Codes live five minutes and are
**used up by any redemption, right or wrong** (taken out before checked). Access tokens are an hour, in memory (a restart costs one
refresh); refresh tokens rotate on use and are saved hashed in `oauth.json` so a restart does not sign every connector out. A
grant remembers **the key it was made with**: remove that key and every session made with it ends; `DELETE
/api/admin/users/{id}/sessions` ends them all and leaves the keys. Redirects: https, http on loopback, or an app's private-use
scheme; never javascript/data/file. `Mcp:PublicUrl` is the issuer and resource behind a TLS proxy — without it they say `http`, and
both vendors refuse that. **Rejected: passwords** — a hash to keep, a reset flow to build, a form to rate-limit, and a second secret
per person that proves nothing the key does not. **Rejected: an outside identity provider** — a phase of its own if asked; nothing
here prevents it. **Rejected: Client ID Metadata Documents** — the hub fetching a URL a stranger names is an SSRF surface, and
dynamic registration is what both vendors' connectors use.

**D6 — the tools are thin over the paths the HTTP surfaces run, so "every model" means the same list and the same rules.**
`list_models` is `ModelDiscovery.Merge` (65 D5) — nodes, node groups that split a model (136), cluster peers' models (132) and named
providers' models — inside the caller's organisation scope (begun in the MCP handler, since `OrganisationMiddleware` keys on
`/api` and `/v1`), minus what the caller's `AllowedModels` hides (the 404 rule, 25), each with its capabilities as the nodes
declared them (40) and where it runs (`node` · `provider` · `cluster`). `chat` builds an Ollama body (`stream:false`, `num_predict`)
and calls `InferenceCore.DispatchAsync` with the request's own `ClientContext` — admission, the warm tier, failover, a provider's turn,
the usage meter and the audit line's model happen there, once. `embed` takes the same admission (the embed routes on `/api` and
`/v1` take none — named in the notes, unchanged here). A failure inside a tool is a result with `isError`, which the model reads; a
malformed call is a JSON-RPC error. **Rejected: an in-process HTTP call to `/v1/chat/completions`** — a second pass through auth with
a credential the tool does not hold (an OAuth token is refused there, D4). `/mcp` is on the audit trail (path only — never a form's
key or a query's state), and a standby refuses it with the other work paths (32).
