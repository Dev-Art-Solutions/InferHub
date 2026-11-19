# InferHub v3.107.0 — people with roles, and the coordinator as an MCP server any assistant can sign in to (phase 154)

Until 3.106 every credential on a hub was a line of config: `Auth:Clients` ran models, `Auth:AdminApiKeys` did everything else.
Adding a person meant an edit and a restart. There was no "may see the fleet but not change it" and no "may list models but not
run them". Nothing an assistant speaks natively reached the fleet either: Claude Code, claude.ai, ChatGPT and both vendors' APIs
speak MCP, and the hub spoke Ollama and OpenAI. 3.107 adds both, each behind its own switch.

## What is new

- **`Users:Enabled`: people made at runtime.** `/users.html` or `POST /api/admin/users {id, role, displayName?, organisation?,
  priority?, collections?, limits?}`. The answer carries the user's first key (`ihu_…`) once; the hub keeps only its SHA-256. Also
  more keys per user, removing one, `PATCH` (role, disabled, limits, …), delete, and `DELETE …/sessions`.
- **Roles over five permissions:** `models`, `inference`, `admin.read`, `admin.write`, `users`. Built in: viewer, user, operator and
  admin. `Users:Roles` adds your own; a built-in role cannot be redefined. A user's key works on `/api`, `/v1` and `/api/admin` as far
  as its role goes and gets a 403 beyond it. It carries the user's limits, collections, priority and organisation exactly like an
  `Auth:Clients` entry, and the audit line names `user:<id>`. A disabled user, a removed key or a deleted user signs in as nobody.
- **`Mcp:Enabled`: `/mcp`**, MCP's Streamable HTTP transport (2025-11-25, 2025-06-18, 2025-03-26), answering JSON. Sessions are
  bound to the caller who opened them. An `Origin` from another site is refused.
- **Tools by permission:**
  - `list_models`: every model the caller may use, from nodes, node groups, cluster peers and connected cloud providers, inside
    the caller's organisation and allowlist, with capabilities and where each runs.
  - `chat`: the same dispatch as `/api/chat`, so limits, failover, providers, usage and audit apply.
  - `embed`: vectors from an embedding model.
  - `fleet_status`: the connected nodes (operators and admins).

  Any key the hub accepts works on `/mcp` with the permissions it already has. An admin key sees `list_models` and `fleet_status`
  and still never runs a model.
- **OAuth 2.1 for connectors.** With users on, the hub is its own authorization server, which is what claude.ai and ChatGPT
  connectors need. It provides:
  - protected-resource and server metadata;
  - open dynamic registration;
  - PKCE S256 only, with single-use codes;
  - one-hour access tokens and rotated refresh tokens.

  The person signs in on the hub's page with **their own key**; there are no passwords. Removing that key ends the sessions made
  with it. `Mcp:PublicUrl` sets the https address behind a proxy.
- **`/users.html`** lists users, keys, roles and MCP sign-ins, and shows the four connection snippets: Claude Code, a claude.ai or
  ChatGPT connector, the Claude API's MCP connector, and OpenAI's Responses API.

## What does not change

Both switches are off by default. Off, there is no route, no file, and the two key guards behave byte for byte as before. Rule 4
gains its eleventh exception: `users.json` and `oauth.json` under `Users:DataDirectory` (the image sets `/data/users`). No new
package.

## Verified

- **Tests:** `UsersAndMcpTests`, 7 tests on a real Kestrel port:
  - keys are hashed and resolve to nobody once disabled or removed;
  - custom roles work and a redefined built-in stops the hub;
  - `users.json` survives a restart;
  - every role's reach on `/v1`, `/api/admin` and `/api/admin/users`;
  - MCP initialize, notifications, sessions bound to their caller, tools per role, chat reaching the node with an Ollama body,
    embed, `-32601`/`-32602`, GET 405, a foreign Origin 403, DELETE;
  - client and admin keys on `/mcp`;
  - the whole OAuth flow: metadata, registration (an http redirect off loopback refused), the authorize page with
    `X-Frame-Options: DENY`, a missing PKCE answered back to the client, a wrong key, the code, a wrong verifier using the code
    up, the token on `/mcp` and refused on `/v1`, a refresh that rotates, and a disabled user's token refused.
- **Live from source:** a hub with `Auth:RequireAuthForLoopback=true`, one node, the machine's Ollama (qwen2.5:0.5b,
  nomic-embed-text):
  - the viewer got 200 on `/v1/models` and 403 on chat; the user got 200 on chat and 403 on admin; the operator got 200 reading
    admin and 403 on users and on a cordon;
  - MCP `chat` answered "Paris." (39 prompt and 3 completion tokens, `served_by: node`), and `embed` returned 2 × 768;
  - `fleet_status` showed the node to the operator only;
  - the 401 named the resource metadata.
- **Claude Code 2.1.105** attached with `--mcp-config` (http, Bearer user key), called `list_models` and `chat`, and quoted the
  model's reply.

## The published image

Filled in after the tag.

## The suite

Filled in after the tag.

## Not established

- **A real claude.ai or ChatGPT connector signing in.** That needs this hub on a public https address. The flow is tested end to end
  against the real endpoints, but not with the vendors' own clients.
- **The Claude API's and OpenAI's MCP connectors.** These also need a public address, and a paid call to each vendor. The snippets
  follow each vendor's documented shape.
- **Users in a cluster.** Each coordinator keeps its own `users.json`, the same named gap as 152's groups.
- **Images, video, speech and jobs as MCP tools.** Not offered; they are async jobs and would follow on the same transport.
- **Embeddings on `/api/embed` and `/v1/embeddings` still take no admission.** This was found while writing `embed`, which does take
  it. Unchanged here.
