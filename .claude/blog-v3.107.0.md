slug: inferhub-3-107-users-roles-and-mcp
title: InferHub 3.107: people with roles, and a coordinator your assistant can sign in to
excerpt: Users made at runtime with a role and their own keys, and an MCP server on the coordinator. Claude Code, claude.ai, ChatGPT and both vendors' APIs can now reach every model a person may use.

<p>Until this release, every credential on an InferHub coordinator was a line of configuration. A client key ran models, and an admin key did everything else. Adding a person meant editing a file and restarting. You could not say "may see the fleet but not change it", or "may list the models but not run them". And none of the assistants people use every day could reach the fleet natively: Claude Code, claude.ai, ChatGPT and the Claude and OpenAI APIs all speak MCP, while the coordinator spoke Ollama and OpenAI.</p>

<p>3.107 adds both. Each is behind its own switch, off by default.</p>

<h2>People, with a role each</h2>
<p>With <code>Users:Enabled</code>, an admin makes people at runtime, on the new <code>/users.html</code> page or through the admin API. The answer carries the person's first key, <code>ihu_…</code>, exactly once. The coordinator keeps only its SHA-256.</p>
<pre><code>POST /api/admin/users
{ "id": "alice", "displayName": "Alice", "role": "user", "organisation": "lab-a" }

201 { "user": { … }, "key": "ihu_…" }</code></pre>
<p>A role is a name over five permissions: <code>models</code>, <code>inference</code>, <code>admin.read</code>, <code>admin.write</code> and <code>users</code>. Four roles are built in:</p>
<ul>
  <li><strong>viewer</strong> lists models.</li>
  <li><strong>user</strong> also runs them.</li>
  <li><strong>operator</strong> also reads the admin API.</li>
  <li><strong>admin</strong> can do everything, people included.</li>
</ul>
<p>You can add roles of your own, such as an auditor who reads but never runs anything. A key works as far as its role allows and gets a 403 beyond that. It also carries the person's limits and organisation, so quotas, organisation boundaries and the audit trail treat a person the same way they treated a configured client. A disabled person, a removed key or a deleted user signs in as nobody.</p>

<h2>The coordinator as an MCP server</h2>
<p>With <code>Mcp:Enabled</code>, the coordinator serves the Model Context Protocol at <code>/mcp</code>. Each caller sees only the tools their role allows:</p>
<ul>
  <li><strong>list_models</strong> lists every model the caller may use, wherever it runs: on the machines, as a big model split across several of them, on another coordinator of the cluster, or at a cloud provider the operator connected.</li>
  <li><strong>chat</strong> sends a conversation to any of them. It takes the same path as the HTTP API, so limits, failover and the audit trail apply.</li>
  <li><strong>embed</strong> returns vectors from an embedding model.</li>
  <li><strong>fleet_status</strong> shows the machines, for operators.</li>
</ul>
<p>For Claude Code, add a server entry to <code>.mcp.json</code>:</p>
<pre><code>{ "mcpServers": { "inferhub": {
    "type": "http",
    "url": "https://hub.example.com/mcp",
    "headers": { "Authorization": "Bearer ihu_…" } } } }</code></pre>
<p>The Claude API takes the same URL in <code>mcp_servers</code>, with the key as <code>authorization_token</code>. OpenAI's Responses API takes it as a tool of type <code>mcp</code>, with the key as <code>authorization</code>.</p>

<h2>claude.ai and ChatGPT sign in</h2>
<p>Connectors in claude.ai and ChatGPT attach to a remote server only through OAuth, so the coordinator now runs its own sign-in. Add a custom connector with the coordinator's <code>/mcp</code> address and nothing else. The app registers itself and opens the coordinator's sign-in page. The person pastes <em>their own</em> key there, so there are no passwords to store, reset or leak. Codes are single-use and need PKCE. Access tokens last an hour and refresh tokens rotate. Removing the key a person signed in with ends every session made with it.</p>

<h2>Tested</h2>
<p>We ran a coordinator with authentication required even on the local machine, one real machine running Ollama, and three people:</p>
<ul>
  <li>The viewer could list models but not run one.</li>
  <li>The user could run models but not open the admin API.</li>
  <li>The operator could read the admin API but could not change it or make people.</li>
</ul>
<p>Over MCP, <code>chat</code> answered "Paris." from the real model, <code>embed</code> returned two 768-dimensional vectors, and each person saw only their own tools. Then we attached Claude Code: it listed the models and asked the small model a question through the coordinator.</p>

<h2>Not yet established</h2>
<ul>
  <li>A real claude.ai or ChatGPT connector signing in. That needs the coordinator on a public https address. The whole flow is tested against the real endpoints, but not with the vendors' own clients.</li>
  <li>Users shared across a cluster. Each coordinator keeps its own list for now.</li>
  <li>Pictures, speech and long jobs as MCP tools. These are next.</li>
</ul>
<p>Release notes and the configuration are on <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.107.0">GitHub</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_users_mcp">inferhub.devart.solutions</a>.</p>
