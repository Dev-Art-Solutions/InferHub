slug: inferhub-3-83-the-hub-keeps-what-it-relayed
id: 6ae3e663c88f7e02a927c117 (EN visible, BG hidden)
title: InferHub 3.83: the hub keeps what it relayed
excerpt: Since 3.82 a sealed node downloads everything through its coordinator, and the next node downloads it again. The hub now keeps each blob by the sha256 the upstream named, and still asks the upstream every time. A re-pull went from 16 seconds to 4.

<p>InferHub 3.82 sealed a node to its own network. Whatever such a node needs from outside, it gets through its coordinator's relay: an Ollama pull, a Hugging Face file, the model catalogue. That left a cost we said nothing about. Ten sealed nodes given the same model pull it ten times, over the one uplink the hub has. A fleet behind a slow link is exactly the fleet that seals its nodes.</p>

<p>InferHub 3.83 lets the coordinator keep what it relayed. Turn it on, and the second node's pull comes from the hub's disk.</p>

<pre><code class="json">Relay__Enabled=true
Relay__Cache__Enabled=true          # off by default
Relay__Cache__MaxGiB=200            # least recently served goes first
Relay__Cache__KeepFreeGiB=20        # never fill the disk past this</code></pre>

<p>Nodes need nothing new. A 3.82 sealed node gets the cache the day its hub turns it on.</p>

<h2>Keyed by what the upstream said, not by the URL</h2>

<p>The obvious cache keys a file by its URL. That is wrong twice here. A Hugging Face path says <code>main</code>, and <code>main</code> moves, so a URL cache would serve last month's weights as today's. And a gated repo checks the token of the node asking. Under a URL cache, the first node with access would open the file for every node behind the hub.</p>

<p>So the cache keeps a blob only by the sha256 the upstream itself named. An Ollama registry blob is addressed by its digest. A Hugging Face LFS file is named on the resolve, in <code>X-Linked-Etag</code>. We checked that on Hugging Face's newer Xet storage before relying on it, and the redirect still carries the hash. Manifests, model pages and the catalogue are small and they change, so they are always relayed live.</p>

<h2>The upstream is still asked every time</h2>

<p>The relay now follows redirects itself instead of leaving it to the HTTP handler. The first hop of every request goes to Hugging Face or the registry, with the node's own token. That hop decides whether this node may have the file and what <code>main</code> points at today. Only the body behind its redirect can come from the hub's disk. A node without access gets the upstream's refusal, even when the bytes sit one directory away.</p>

<p>The node's token still reaches the upstream's own host and never the CDN. .NET's handler used to drop it on the way, and the hand-written redirect drops it the same way.</p>

<h2>Ollama asks for everything in parts</h2>

<p>A cache that keeps what passes through it would keep nothing of an Ollama pull. Live, Ollama asked for every layer as a range, even one of 89 bytes. So on a miss the hub relays the ranges exactly as before, and fetches the whole blob once on its own, beside them. The first node never waits for a download it did not ask for. Its uplink carries that first pull twice, which is the price of the second pull costing nothing. A whole download, such as a Hugging Face file, is kept as it passes.</p>

<p>Every byte is hashed while it is written. A blob is kept only if the hash matches the name the upstream gave it. A mismatch is still relayed (the node checks its own download, as it always did), counted, and not kept.</p>

<h2>Checked live</h2>

<ul>
<li>A sealed node pulled <code>smollm:135m</code> through the hub in 16 seconds. We deleted it from Ollama and pulled again: 4 seconds. All five blobs came from the hub's disk, and Ollama verified each digest itself. A chat through the hub answered.</li>
<li>A 105 MB GGUF from Hugging Face through the relay took 139 seconds the first time and 0.63 seconds the second. Both copies hash to the <code>X-Linked-ETag</code> Hugging Face sent.</li>
<li>Answers carry <code>X-InferHub-Relay-Cache: hit</code> or <code>miss</code>. <code>/api/status</code> has a <code>relay.cache</code> block, and <code>/metrics</code> has nine <code>inferhub_relay_cache_*</code> series. Both appear only while the cache is on.</li>
</ul>

<h2>Not covered, said out loud</h2>

<ul>
<li>Several sealed nodes pulling one blob at the same moment are covered by a test, not by a live run.</li>
<li>The refusal of a gated repo over a held blob is tested against a fake upstream. The live Hugging Face file was public.</li>
<li>A signed CDN link expiring in the middle of a 20 GB fill was not tried.</li>
<li>In a high-availability pair each coordinator keeps its own cache, so a promoted standby starts cold.</li>
</ul>

<p>This is rule 4's sixth exception to "the hub keeps nothing on disk". It is off by default and bounded, and deleting it costs a download, never a wrong answer.</p>

<p>Release notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.83.0">v3.83.0</a>. Documentation: <a href="https://inferhub.devart.solutions/#idocs_relay_cache">The hub keeps what it relayed</a>.</p>
