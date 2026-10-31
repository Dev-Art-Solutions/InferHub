slug: inferhub-3-85-who-asked-and-which-node-answered
id: 6ae6486bfd07f8d085f6a91e (EN visible, BG hidden)
title: InferHub 3.85: who asked, and which node answered
excerpt: The coordinator can now keep an audit trail: one line per request with who asked, from where, for which model and which node ran it, and one per node that joined, was turned away or left. Never what was said.

<p>An InferHub coordinator has always known who called it. Every request is authenticated with a key and metered for that client. But the usage records are counts per client and model, and what the hub remembered about admin actions was one line per node, in memory. Neither could answer the questions an operator of a closed cluster gets asked. Who used this key at 03:12, and from where? Which machine ran that request? Who cordoned gpu-2? Which box tried to join with the wrong secret?</p>

<p>InferHub 3.85 writes the answers down. Turn on <code>Audit:Enabled</code> and the coordinator appends one JSON line per request to a file per day.</p>

<pre><code class="json">Audit__Enabled=true          # off by default
Audit__RetentionDays=90      # day files older than this are deleted</code></pre>

<pre><code class="json">{"at":"2026-10-31T19:45:57Z","event":"request","who":"alice","remote":"10.0.0.5",
 "method":"POST","path":"/api/chat","status":200,"ms":20637,
 "model":"qwen2.5:0.5b","capability":"chat",
 "nodes":["gpu-d","gpu-c"],"servedBy":"node"}</code></pre>

<h2>What a line says</h2>

<p>When the request came, and who sent it: the client the key belongs to, or for an admin key <code>admin:</code> with the first eight hex digits of the key's SHA-256, which tells two operators apart without storing anything a key could be recovered from. A key that matched nobody is <code>-</code>. Then the address it came from, the method and the path, the status and how long it took (a streamed answer is measured to its last byte), the model it asked for and the kind of work.</p>

<p>And every node a job of the request was sent to, in order. If a node drops while it is answering and the coordinator hands the request to another, both are on the line, and the last one is the node that answered.</p>

<h2>What it never says</h2>

<p>No prompt, no answer, no request body, no query string, no header value, no key, and no hash of any of them. The path is kept and the query string is not, because a query string is where a caller's own text can travel. The tests send a prompt, a query value and both kinds of key through the real key checks and then search the files for them.</p>

<h2>What is recorded</h2>

<p>Both client APIs, the admin API, the relay a sealed node downloads through, and ChatGPT pairing. Reads of the fleet status and of the admin API are not recorded: the console polls them every five seconds, and those lines would bury the ones that matter. There are two exceptions. A refused request is recorded whatever its method, because a wrong key is the first line an auditor looks for. A read of the trail itself is recorded too, because who looked at the record is part of the record. The trail is the first thing in the coordinator's pipeline, so it also sees requests the key checks turn away.</p>

<p>The coordinator's door for nodes is on the trail as well: a node registered (with its id and name), a node refused for a wrong or missing enrollment secret (with the address it came from), and a node gone.</p>

<p>The lines are written by one background writer, so a slow disk never slows a request. If the writer cannot keep up, or cannot write at all, a line is dropped and counted, and the count is on <code>/metrics</code>. Reading it is <code>GET /api/admin/audit</code> with filters for time, client, node, model, event and status, newest first. The files are plain JSON lines, so a log shipper can take them off the box.</p>

<h2>Checked on a real coordinator</h2>

<p>A coordinator from source with the trail on and keys required even from the same machine, nodes on a real Ollama with <code>qwen2.5:0.5b</code>, and one node with the wrong secret:</p>

<ul>
<li>Each chat's line named the node that ran it. A streamed answer's line said 51 seconds, its full length.</li>
<li>A stolen client key and a guessed admin key were recorded as <code>-</code> with 401. A cordon and an uncordon carried the admin key's fingerprint. An admin read with the right key was not recorded.</li>
<li>Both nodes' registrations were there with their names. The node with the wrong secret wrote a refusal every five seconds, because it retries every five seconds.</li>
<li>A long story was sent to one node, which was stopped in the middle of it. The coordinator finished it on the other node, the client got its answer, and the line listed both nodes in that order.</li>
<li>A search of the files for the prompts, the query value and both keys found nothing.</li>
</ul>

<h2>What it does not do</h2>

<p>It does not say which node ran an asynchronous image or video job: the request that submits one is recorded when it is accepted, before the job runs. It is not tamper-evident: there is no hash chain or signature, and anyone with root on the coordinator's machine can edit a file. If that matters, ship the lines off the box as they are written. Each coordinator in a high-availability pair writes its own trail.</p>

<p>The trail is off by default. With it off, nothing is created and nothing changes. The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.85.0">v3.85.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_audit_trail">inferhub.devart.solutions</a>.</p>
