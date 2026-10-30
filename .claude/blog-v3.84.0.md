slug: inferhub-3-84-models-supplied-by-the-hub
id: 6ae524626c9968c14c82cc60 (EN visible, BG hidden)
title: InferHub 3.84: models supplied by the hub
excerpt: A sealed node downloads through its coordinator, but until now the coordinator still fetched from the internet when a node asked. Now an admin fetches a model once, or imports it from a disk, and the fleet pulls only from there. It works with no internet at all.

<p>InferHub 3.82 sealed a node to its own network: whatever it needs from outside, it gets through its coordinator's relay. InferHub 3.83 let the coordinator keep what it relayed. One thing was still true after both. The coordinator was a relay. It fetched a model at the moment a node asked for it, it asked the upstream every time, and a coordinator with no internet had nothing to relay. A site with no internet at all could not manage models through InferHub.</p>

<p>InferHub 3.84 makes the coordinator the fleet's source of models. An admin fetches a model once, or imports it from a disk, and the hub answers its sealed nodes from its own disk without asking anybody.</p>

<pre><code class="json">Relay__Enabled=true
Relay__Supply__Enabled=true             # off by default
Relay__Supply__Only=true                # optional: nothing that is not supplied, ever
Relay__Supply__ImportDirectory=/import  # where a carried-in disk is mounted</code></pre>

<p>Nodes need nothing new. The hub answers the same requests a 3.82 sealed node already sends through the relay.</p>

<h2>Fetched once, and every byte checked</h2>

<p>A fetch is one admin call with a body like <code>{"ollama": "qwen3:8b"}</code>, or <code>{"huggingFace": "owner/repo", "revision": "main", "include": ["*Q4_K_M.gguf"]}</code>. It runs as a job, and the console has a panel for it. The hub fetches from the registry and Hugging Face, or from a mirror beside it if that is what its relay points at.</p>

<p>Every byte is checked before it is supplied. An Ollama blob must hash to its digest. A Hugging Face file must hash to the sha256 Hugging Face lists for it, or, for a small file, to its git object id. Every size must match. A model is supplied whole or not at all: one file that does not match fails the job, and nothing of it is kept.</p>

<h2>Imported from a disk</h2>

<p>An air-gapped site has a machine somewhere that does have internet, and that machine already has the models in a standard layout. So the import reads those layouts: an Ollama models directory, a Hugging Face cache as the Hugging Face tools leave it, or another InferHub hub's supply directory. You carry the disk in, mount it under <code>Relay:Supply:ImportDirectory</code>, and ask for a path under it with a body like <code>{"path": "disk1", "name": "qwen3:8b"}</code>. Each file is hashed as it is copied, and the disk is left as it was. A path that leaves the import directory is refused, because an admin key is not a shell on the hub's machine.</p>

<p>We looked at this box's own Ollama directory before the live import and found that Ollama 0.40 writes a new pull to a new place, <code>manifests-v2/ollama.com/</code>, beside the old <code>manifests/registry.ollama.ai/</code>. The manifest inside is the same. The import reads both, and the tests run both.</p>

<h2>The upstream is not asked</h2>

<p>This is the difference from 3.83's cache, and it is on purpose. The cache asks the upstream on every request, because the cache was filled by whichever node happened to ask first. A gated repo checks each node's token, and <code>main</code> can move, so the cache never trusts what it holds on its own.</p>

<p>A supplied model is different, because a person decided to supply it. That decision covers access (the hub fetched a gated repo with its own token) and the version (<code>main</code> stays the commit it pinned that day, <code>qwen3:8b</code> stays that manifest). So the hub serves it without asking anybody. To update a model, fetch it again. Blobs no other model needs are then deleted.</p>

<h2>Only closes the last door</h2>

<p>With <code>Relay:Supply:Only=true</code>, anything not supplied is a 404 that names the setting, and the hub fetches nothing on a node's behalf. A sealed node's pull then ends with the hub's own sentence, for example "the coordinator does not supply 'tinyllama:latest' … an admin supplies a model with POST /api/admin/supply", instead of Ollama's "file does not exist". The admin's own fetch still reaches the upstream, or the mirror.</p>

<p>Browsing the model catalogue from a sealed node does not work under <code>Only</code>, because catalogue pages are not models. The admin's list of supplied models is the catalogue there.</p>

<h2>What you can see</h2>

<p>Answers from the supply carry <code>X-InferHub-Relay-Supply: supplied</code>, and a refusal carries <code>not-supplied</code>. <code>/api/status</code> has a <code>relay.supply</code> block and <code>/metrics</code> has six <code>inferhub_relay_supply_*</code> series. Both appear only while the supply is on. Nothing is evicted, because under <code>Only</code> an evicted model would be gone from the fleet. Instead, a fetch or an import that would leave less than 20 GiB free is refused before it starts.</p>

<h2>Checked live</h2>

<p>On one machine, from source: a hub with the supply on, a sealed node, and the real Ollama 0.40.12.</p>

<ul>
  <li>Online, the hub fetched <code>smollm:135m</code> (92 MB) and <code>all-minilm:33m</code> (67 MB) from the Ollama registry, and a 105 MB GGUF from Hugging Face, each in about five seconds.</li>
  <li>Then we restarted it with all three upstreams pointed at a closed port, and <code>Only</code> on. The sealed node pulled <code>all-minilm:33m</code>, and every file it asked for came from the supply. Ollama checked the digests itself, and an embedding request through the hub answered.</li>
  <li>The node asked for a model nobody had supplied. The hub refused, and the node's pull failed with the hub's sentence.</li>
  <li>The hub imported <code>qwen2.5:0.5b</code> (398 MB) from this box's real Ollama directory. Read back through the relay, its weights hash to their digest.</li>
  <li>The GGUF came back through the relay in 290 ms with the upstream unreachable, hashing to the etag Hugging Face had sent for it.</li>
  <li>Then the published 3.84.0 coordinator image, as a container running as its non-root user, imported the same model from the Ollama directory mounted read-only, served it to the sealed node, and refused the model nobody had supplied.</li>
</ul>

<h2>Not established</h2>

<p>"Offline" here was a hub whose upstreams pointed at a closed port, on a machine that has internet. It was not a machine with the cable pulled. The Hugging Face cache import ran in tests with plain files, not on a real cache with its links. A gated repo was not fetched with a real token. No node with a GGUF engine pulled a supplied Hugging Face file end to end: the node's own Hugging Face client did it in the tests, and the live check read the file directly. Nobody clicked the console panel. The largest live file was 398 MB.</p>

<p>The release, with the full notes: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.84.0">InferHub v3.84.0</a>. The docs: <a href="https://inferhub.devart.solutions/#idocs_relay_supply">Models supplied through the coordinator</a>.</p>
