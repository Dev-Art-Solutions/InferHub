# Blog post — v3.12.0

**Slug:** `inferhub-3-12-a-corpus-on-every-node`
**Title (EN):** `InferHub 3.12: a corpus on every node, assigned from one place`
**Excerpt (EN):** `Retrieval used to be the hub's, or a standalone node's, and never both — because a node holding replicas derived from its hub and an authority of its own is one collection name with two truths. 3.12 does not bend that rule. It makes the hub the one who assigns the corpus, and therefore the one who knows who owns it.`

**Publish visible in one shot** (`isVisible_en: true`, `isVisible_bg: false`) — the connector is
insert-only and the slug locks on creation, so a hidden draft cannot be flipped later. `list_posts`
first.

**No shell snippets in the HTML at all.** The blog sits behind a Cloudflare WAF, and a `curl` line in
post HTML has had the request blocked twice now — once with an auth header and once without. Every
example below is the **document** or the **response**, with the route named in prose above it.

---

```html
<p>InferHub has had retrieval since 2.0, and a standalone node has been able to run its own since 3.6. What it has never had is the thing people actually asked for: <strong>a corpus on a node that is still part of the fleet</strong> — a box in an office, holding that office's documents, answering grounded questions about them, without a second product to operate.</p>

<p>The reason it took six releases is worth more than the feature.</p>

<h3>The rule that was in the way</h3>

<p>Version 3.6 gave a standalone node the whole retrieval stack, and refused — at startup, loudly — to let a <em>meshed</em> node turn it on. Both keys named in the error, no way to override it.</p>

<p>That refusal was not caution. A meshed node already holds vector replicas <em>derived</em> from its coordinator: the hub is the authority, the node's copies are disposable, and a healing pass rebuilds them from the hub whenever they drift. Give that same process an authoritative store of its own and one collection name means two different things. A document ingested locally is invisible to the fleet. A replication pass pushes the hub's idea of that collection over the node's. Nobody is told, because from each side everything looks fine.</p>

<p>So the honest options were: don't ship it, or ship it and hope nobody puts the same name in both places.</p>

<h3>What actually changed</h3>

<p>The rule is kept. What changed is <em>who knows</em>.</p>

<p>The refusal is still there — a node that switches retrieval on <em>by hand</em> while it is in a mesh still fails to start, and there is a test in this release whose only job is to keep that true. What 3.12 adds is the case that refusal could not express: a corpus <strong>the hub assigned</strong>, and therefore a corpus the hub has written down.</p>

<p>A node profile — the one-document-per-fleet thing 3.11 introduced — grows a <code>retrieval</code> section. This is a <code>PUT</code> to <code>/api/admin/profiles/edge-boxes</code>:</p>

<pre><code class="json">{
  "selector": { "labels": { "site": "sofia" } },
  "retrieval": {
    "enabled": true,
    "provider": "qdrant",
    "url": "http://qdrant.sofia.internal:6333",
    "credentialRef": "sofia-qdrant",
    "collections": ["site-sofia-docs"],
    "embeddingModel": "nomic-embed-text"
  }
}</code></pre>

<p>Every node whose labels match brings the corpus up — while it goes on answering chat. No file edited on the machine, no restart, no dropped inference. Switch it off and the corpus stops, the retrievals already running finish against the store they started on rather than faulting, and the data is still there when you switch it back on.</p>

<p>And on the hub, one new fact: <strong>every collection name now has an owner</strong>, either <code>hub</code> or <code>node:{id}</code>. From that one record, three things follow that used to be impossible to guarantee:</p>

<ul>
  <li>A hub-side create of a node-owned name is a <code>409</code> <em>naming the owner</em> — not a mystery conflict that sends somebody looking for a collection which is not missing, just somewhere else.</li>
  <li>Replication never targets it. There is nothing hub-side to derive replicas from, so a placement pass over one would push an <strong>empty</strong> snapshot at its owner. That is the hub deleting your corpus while reporting success, and it is the exact failure the 3.6 refusal existed to prevent.</li>
  <li>Healing never targets it either, and an operator who asks for a rebuild by hand gets the owner's name back instead of obedience.</li>
</ul>

<p>One sentence to check any future change against: <strong>one authority per collection name, and the hub knows who it is.</strong></p>

<h3>The client API does not move</h3>

<p>This is the part I would want to read first as a user of it. Documents still go to <code>/api/collections/{c}/documents</code>. Searches still go to <code>/api/collections/{c}/search</code>. The same bodies, the same statuses, the same client scoping and quotas — whether the chunks are in the hub's own store or on a box in another city.</p>

<p>The hub sees a node-owned collection, dispatches the work down the connection that node already opened, and the node runs the <em>same</em> ingestion and retrieval pipelines the coordinator runs. Not a smaller reimplementation of them: the same code, which is why chunking, deterministic ids, the stale-chunk sweep, k clamping, fusion and the rerank fallback cannot quietly drift apart between the two places your documents might live.</p>

<p>Two refusals are said out loud rather than left to be discovered.</p>

<p><strong>PDF on a node-owned collection is a 415.</strong> The PDF text extractor ships with the coordinator only. The hub <em>could</em> extract the text here and send chunks down instead — and that is exactly what makes it wrong: it would be a second ingestion path, with different chunking and a different failure mode from every other document on that node. This project refused OCR in 2.5 for the same reason. A bad extraction succeeds <em>quietly</em> and fills a corpus with plausible nonsense.</p>

<p><strong>An owner that is not connected is a 503 naming the node</strong> — never a quiet answer from the hub's own store, even when a collection of that name happens to exist there. A confident answer from the wrong corpus is the failure nobody notices for three weeks.</p>

<h3>The hub names a credential. It never carries one.</h3>

<p>That <code>credentialRef</code> in the profile is a <em>name</em>. It resolves on the node, against configuration that lives on the node:</p>

<pre><code class="json">"LocalApi": {
  "Retrieval": {
    "Credentials": { "sofia-qdrant": "the-actual-key" }
  }
}</code></pre>

<p>A ref the box cannot resolve is a <strong>refusal naming the key</strong> — never a quiet fall back to an unauthenticated connection to somebody's vector database.</p>

<p>The alternative was one line shorter and much worse: a hub that pushes the API key down the link is a secret distributor. The key lands in profile persistence, it comes back out of an admin API response, and every node the selector matched now holds a credential it may not need. It is also the 3.11 rule by another route — the hub would be <em>granting</em> rather than narrowing.</p>

<p>Same reasoning covers the disk: there is no field anywhere in a profile for a data directory. Where bytes land on a machine stays the operator's.</p>

<h3>A dependency declined two releases ago is why this one was free</h3>

<p>A node can run its corpus on its own disk, or on Qdrant. Not on Postgres — that connector needs a package which is scoped to the coordinator by name, and the node refuses <code>postgres</code> <em>by name</em>, with that reason, rather than reporting an unknown value. Somebody who typed the name of a provider this product genuinely has is owed the reason it is not available on a box.</p>

<p>Qdrant, though, cost nothing at all — and the reason is a decision made in 3.1, for what looked like an unrelated purpose. Qdrant's official client is gRPC, which would have dragged protobuf into the coordinator; its REST API is plain JSON, and this codebase already speaks HTTP-to-a-server by hand. So the connector was hand-rolled over <code>HttpClient</code>, and the dependency count stayed where it was.</p>

<p>Which is why, this release, the whole Qdrant store could simply <strong>move</strong> into the shared library and run on a node unchanged. Not be ported. Not be reimplemented smaller. Moved — the same file, and if a single assertion in its test suite had needed editing, the move would not have been a move.</p>

<p>Zero new dependencies, for the ninth release running.</p>

<h3>One behaviour change worth knowing</h3>

<p>With retrieval off, a node's <code>/api/collections</code>, <code>/api/collections/{c}/documents</code> and <code>/api/vector/{c}/…</code> routes now answer <strong>501</strong> with the retrieval refusal, where they used to answer <code>404</code>.</p>

<p>The mechanical reason is that a coordinator can now start a corpus on a <em>running</em> node, and a web framework cannot add an endpoint after the application has started — so the routes have to exist before there is anything behind them. The better reason is that 501 was always the right answer: "this host could serve that if it were configured to" is a different fact from "wrong URL", and it is the answer tools and audio already gave in 3.9 and 3.10.</p>

<h3>What the hub knows about your node's corpus</h3>

<p><code>/api/status</code> grows a per-node corpus block: the engine, whether it is running, the collections with their record counts, and the last thing that went wrong. It is what the node <em>reported</em> — the hub never queries a node's corpus to build that page, because that would make the status page a synchronous dependency on a box that might be asleep, and a status page has to answer when the fleet is what is broken.</p>

<p>A start that fails — unreachable engine, credential the box does not have, a dimension that does not match — leaves the node with <strong>no corpus and a reported refusal</strong>, and a node that is still serving chat. Never a half-started corpus that answers some queries.</p>

<p>InferHub 3.12 is on GitHub and GHCR. If you are on 3.11 and assign no corpus, upgrading changes nothing at all — which is the other thing worth saying about a release that touches the word "authority".</p>
```
