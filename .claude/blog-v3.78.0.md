slug: inferhub-3-78-models-kept-for-the-owner
id: 6ade681b8b5c4de290773d65 (EN visible, BG hidden)
title: InferHub 3.78: the big model is mine, the small one may serve anybody
excerpt: A node in a network of friends' PCs can now keep some of its models to itself. The network is never told they are there, and the owner's own apps still use them.

<p>InferHub 3.76 and 3.77 let the owner of a node say <em>whether</em> the network may use it and <em>when</em>. In a network of friends' PCs, the next question is <em>what</em>. A machine often holds one big model its owner uses for their own work and a few small ones that cost little to lend. Since 3.78 that is one setting on the node:</p>

<pre><code class="json">"Node": { "Sharing": { "Models": { "Exclude": ["qwen3:32b"] } } }</code></pre>

<p><code>Exclude</code> shares every model except these. <code>Include</code> shares only these. Leave both empty and the network may use every model, which is how 3.77 behaved. A name in both lists stops the node from starting, so a node never shares something its owner also said to keep.</p>

<h2>Absent, not paused</h2>

<p>3.76's pause keeps a node connected and its models visible, while the hub sends it nothing new. A kept model is handled differently: the node never tells the hub it is there. That choice decides three things:</p>

<ul>
<li><strong>Every hub obeys it.</strong> No coordinator of any version routes to a model it was never told about, so a 3.78 node needs no hub upgrade.</li>
<li><strong>The network sees no models it cannot use.</strong> A kept model is not listed in the hub's <code>/api/tags</code> and is not named under any capability. A request for it at the hub gets the same <code>404</code> as a model no node has.</li>
<li><strong>The owner keeps everything.</strong> The node's own local API still lists and serves a kept model, and so do the control API and the desktop app. Keeping a model takes it away from the network, not from the owner.</li>
</ul>

<p>A change reaches the hub at once, because the node reports its models again as soon as the list changes. A network job already running on a model that has just been kept finishes.</p>

<h2>What running it showed</h2>

<p>We ran a 3.78 hub and node from source against the machine's real Ollama, with the node serving three small models. Then we kept two of them through the control API:</p>

<pre><code class="json">PUT /api/control/sharing
{ "models": { "exclude": ["gemma:2b", "nomic-embed-text:latest"] } }</code></pre>

<p>0.97 seconds later the hub listed only <code>qwen2.5:0.5b</code>. A chat for <code>gemma:2b</code> at the hub was a <code>404</code>, and so was an embedding with <code>nomic-embed-text</code>. The same two requests on the node's local API both answered. We restarted the node: the list was still in its settings file, and the hub again saw only the one model. We gave <code>gemma:2b</code> back, and 0.85 seconds later the hub was answering chats with it. The published 3.78.0 images, run natively, behaved the same way.</p>

<p>Running it also corrected two claims before release:</p>

<ul>
<li><strong>Every model the node does not serve read as shared.</strong> The node was limited to three of the machine's 29 Ollama models, and the control API marked the other 26 <code>"shared": true</code>. The network is never told about those either, so the field is now left out for them and the app shows no box.</li>
<li><strong>Keeping the only embedding model does not withdraw <code>embed</code>.</strong> Our first draft said it did. Ollama declares what it can do for the whole engine, so the node still declared <code>chat, embed</code> over the one model it shared. What holds is narrower: the kept model is named under no capability, and the hub refuses it.</li>
</ul>

<h2>In the app</h2>

<p><a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/desktop/v0.3.0">InferHub Desktop 0.3.0</a> puts a <strong>Shared</strong> box beside each model on its Models tab. The Overview names what is kept: <em>Kept for this machine: nomic-embed-text:latest — the network may use 2 of 3 models</em>. If you wrote an <code>Include</code> list, the box edits that list. Unticking its last name is refused, because an empty <code>Include</code> means every model. Otherwise the box puts a model on <code>Exclude</code>, so a model you pull later is shared, as before. The packaged Windows app, with its own 3.78 node, showed a kept model unticked and every other model shared.</p>

<h2>What we did not establish</h2>

<ul>
<li>A network job that was already running on a model when it was kept. It should finish, the same way a paused node finishes its jobs, but it was not run live.</li>
<li>Clicking the box in the app. The app drew the boxes from a live node, and the request the box sends was sent with <code>curl</code> and applied.</li>
<li>The desktop app on Linux and macOS is still built by CI only.</li>
</ul>

<p>With no list set, a node behaves exactly as 3.77. <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.78.0">Release notes</a> · <a href="https://inferhub.devart.solutions/#idocs_sharing_models">documentation</a>.</p>
