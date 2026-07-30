# Blog post — v3.8.0

**Slug:** `inferhub-3-8-a-node-that-says-what-it-can-do`
**Title (EN):** `InferHub 3.8: a node that says what it can do`
**Excerpt (EN):** `A node used to advertise a list of model names, and the coordinator routed on that alone — which quietly assumed every model on a node does the same kind of work. It does not. A box holding only nomic-embed-text was a perfectly good candidate for a chat request, and the error arrived from the backend after a dispatch. Routing now asks (capability, model), and nothing about what a model is for is guessed.`

**Publish visible in one shot** (`isVisible_en: true`, `isVisible_bg: false`) — the connector is
insert-only and the slug locks on creation, so a hidden draft cannot be flipped later. `list_posts`
first.

**No shell snippets in the HTML at all.** The blog sits behind a Cloudflare WAF. The known trigger
was `curl -H 'Authorization: …'`; **on this post it also blocked a plain
`curl -s localhost:5080/api/status | jq .capabilities`** — no auth header anywhere. Replacing both
`curl … | jq` blocks with the JSON they print went through unchanged on the retry. Show the
*response*, not the command.

**Published:** `2026-07-30`, id `6a6b5e14ef1bf8e0c6096af7`, EN visible.

---

```html
<p>Until this release, an InferHub node advertised one thing about itself: <strong>a list of model names</strong>. The coordinator routed on that and nothing else, and it worked for three years of releases because of an assumption nobody had written down — that every model on a node does the same kind of work.</p>

<p>It does not. A box holding only <code>nomic-embed-text</code> was a perfectly good candidate for a chat request naming that model. The router had no way to know otherwise, so it dispatched, and the error came back from the backend seconds later. That is the small version of the problem.</p>

<p>The large version is the one this release exists for: a node that runs a speech model — or anything else that is not a language model — has no way to say so.</p>

<p>So the unit of routing is now the pair <code>(capability, model)</code>.</p>

<pre><code class="bash">curl -s localhost:5080/api/status | jq .capabilities
# [ { "capability": "chat",  "nodes": 2, "models": ["llama3.2", "qwen2.5"] },
#   { "capability": "embed", "nodes": 3, "models": ["llama3.2", "nomic-embed-text", "qwen2.5"] } ]

curl -s localhost:5080/v1/models | jq '.data[] | {id, capabilities}'
# { "id": "nomic-embed-text", "capabilities": ["embed"] }</code></pre>

<h3>Nothing is guessed</h3>

<p>The tempting version of this feature is the one where the node works it out for you. Ollama reports a model list; surely a model with <em>embed</em> in its name is an embedding model?</p>

<p>That would be a lookup table that gets built, believed, and is wrong for somebody — and it is a mistake this project already declined to make once. There is no registry of which models accept images either: send an image to a text-only model and the model refuses, and we forward the refusal rather than keeping our own list of what everything supports. A list like that drifts the moment the ecosystem moves, and a wrong entry is worse than no entry, because it fails in a way nobody can see from the outside.</p>

<p>So a node declares <code>chat</code> and <code>embed</code> over everything its backend actually reports, and the one thing it genuinely cannot work out for itself is one line of configuration:</p>

<pre><code class="json">"Node": { "Capabilities": { "Disabled": ["chat"] } }</code></pre>

<p>That node is then never sent a chat job. The key is <strong>subtractive only</strong>: you can narrow what a node is used for, and you cannot make it claim something it has not got. Disabling both <code>chat</code> and <code>embed</code> fails startup, because a node routed for nothing is a machine burning power for nothing. And an unknown name — <code>"chatt"</code> — fails startup too, rather than disabling nothing at all on a box whose operator believes they have just moved the traffic off it.</p>

<h3>A capability nobody has is a 503, not a 404</h3>

<p>If the model is on the fleet but no node will do <em>this</em> with it, the answer is <code>503</code> with <code>Retry-After</code>, naming the capability:</p>

<pre><code>no node currently provides 'chat' for model 'nomic-embed-text'</code></pre>

<p>That is deliberately the same shape as "every node is busy", because it is the same kind of fact: a statement about the fleet right now, not a statement about what exists. A model that genuinely is not on the fleet is still the <code>404</code> it has always been, byte for byte — "not found" must not start meaning "not right now".</p>

<p>The check also runs <em>after</em> admission, which matters more than it looks. InferHub already answers "you are not allowed this model" and "this model does not exist" identically, on purpose, so a tenant cannot map what other tenants have. If the capability check ran first, the new 503 would have become a way to ask that question.</p>

<h3>Solo mode enforces the same key</h3>

<p>A node can also run with no coordinator at all, serving the API itself. There is no router there to filter anything — so the node enforces the key at its own edge, with the same status and the same <code>Retry-After</code>, in both dialects.</p>

<p>The alternative was a key that is honoured in one deployment and silently ignored in another, which is worse than the asymmetry. (Its own corpus is exempt: a standalone node doing RAG still embeds its own documents with <code>embed</code> disabled, because a node's own corpus is not somebody sending it work.)</p>

<h3>Upgrading: nothing to do</h3>

<p>A node that declares no capabilities at all — every node before 3.8, and every 3.8 node whose operator has not touched the new key — is read as chat + embed over everything it reports. That is exactly the old behaviour, so a <strong>3.7 node against a 3.8 coordinator</strong> registers and serves normally and a fleet upgrades one box at a time.</p>

<p>This is worth saying plainly because it is the part that took the most care. The new field is optional on the wire, and <em>absent</em> and <em>empty</em> mean different things: absent is "this node has not been asked", empty is "this node serves nothing". The default is materialised in exactly one place in the coordinator, so no code anywhere branches on whether a node is an old one — and the load-bearing test in the new suite is not about the new behaviour at all, it is the one that pins the old.</p>

<p>Everything else is additive: <code>/api/status</code> grows a fleet capability block and a per-node list, <code>/v1/models</code> grows a <code>capabilities</code> field (omitted entirely for a model nothing can serve, rather than sent empty), and the console and status page grow a column. <strong>Zero new dependencies</strong>, as usual.</p>

<h3>What this is for</h3>

<p>This is the first release of a track rather than a feature on its own. Routing has to learn that "which model" and "what kind of work" are two different questions before a node can run anything that is not a language model.</p>

<p>Next on that track: a tool runtime that lets a node drive a supervised subprocess — Python, in practice, because that is where the libraries are — and then speech-to-text and text-to-speech behind the OpenAI audio API, on your own hardware, with one changed base URL.</p>

<p>InferHub is on <a href="https://github.com/Dev-Art-Solutions/InferHub">GitHub</a>, and the docs are at <a href="https://inferhub.devart.solutions">inferhub.devart.solutions</a>.</p>
```
