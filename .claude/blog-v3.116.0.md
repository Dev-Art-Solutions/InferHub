id: 6b08bc15fbb7d277f95ea6cb
slug: inferhub-3-116-a-burst-waits-its-turn
title: InferHub 3.116: four requests at once, and a node with room for two

<p>Since 3.74 a coordinator can hold a chat while every node that has its model is busy, and send it to the first node that frees a slot. A request waits at the coordinator rather than inside one engine's queue, so a second node that finishes first is not left idle.</p>
<p>3.112 found where that stopped working: requests that arrive together. Four chats sent at the same moment to a node with two slots all looked for a free slot before the first one was counted. All four saw room, and all four went. The coordinator held none of them, and two waited inside the engine behind the other two.</p>

<h2>What changed</h2>
<p>A request that finds a free slot now takes the same line as one that finds none. It looks, it is sent, and the next request looks only after this one is counted against its node. That was already the rule for requests that were waiting; now it is the rule for every request that arrives.</p>
<pre><code>"Queue": { "SlotWaitSeconds": 20 }</code></pre>
<p>There is nothing new to configure. It applies wherever the slot wait is on and a node that has the model declared how many slots it runs. A coordinator that never turned the slot wait on behaves exactly as before.</p>

<h2>Measured</h2>
<p>A coordinator and a node with two slots. Behind the node, a stand-in engine that answers in 3 seconds and counts how many requests it is running at once.</p>
<ul>
<li><strong>Four chats at once on 3.115:</strong> four running in the engine, none held by the coordinator.</li>
<li><strong>Four chats at once on 3.116:</strong> two running, two held. The two held ones started the moment the first two finished.</li>
<li><strong>Eight at once on 3.116:</strong> never more than two running; six held, every one of them sent, none refused.</li>
</ul>
<p>The published 3.116.0 coordinator gave the same numbers. Its status says it plainly: <code>waited</code> is how many requests it held, <code>admitted</code> how many it sent once a slot freed, and <code>sentAnyway</code> how many it gave up holding. That was zero.</p>

<h2>What we have not shown</h2>
<ul>
<li>A real engine. The stand-in only counts, so this says nothing about how fast llama.cpp or Ollama answers two instead of four.</li>
<li>Several nodes, and a cluster of coordinators. Each coordinator keeps its own line.</li>
</ul>
