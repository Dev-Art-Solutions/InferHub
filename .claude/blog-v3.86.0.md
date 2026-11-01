slug: inferhub-3-86-essential-work-first
id: 6ae780db9826db8aae9e05e5 (EN visible, BG hidden)
title: InferHub 3.86: when capacity runs short, essential work first
excerpt: Give each client a class: essential, normal or background. When nodes fail or every one is busy, background work waits and leaves the last free places to the work that must keep running.

<p>Until now an InferHub coordinator treated every request the same. When the fleet was short of room, the next request in took the next free place, whoever sent it. That is fine when everything can wait a little. It is not fine when a cluster runs work that must not stop next to work that can, such as an assistant people rely on next to a batch job summarising last week's documents. When a node fails, the batch job should be the one that waits.</p>

<p>InferHub 3.86 adds priority classes. Each client gets one: <code>essential</code>, <code>normal</code> (the default) or <code>background</code>.</p>

<pre><code class="json">Priority__Enabled=true                       # off by default
Auth__Clients__0__Priority=essential
Auth__Clients__2__Priority=background
Priority__ReserveSlots=1
Priority__BackgroundMaxWaitSeconds=600</code></pre>

<h2>What "short of room" means</h2>

<p>The coordinator already knows how much each node can take: a node may declare a concurrency cap, and every node says how many requests each loaded model can run at once. So for a model it counts the free places on the nodes it can route to right now, taking for each node the lesser of what is left of its cap and what is left of the model's slots.</p>

<p>Background work goes only while more than <code>ReserveSlots</code> places are free (one by default). Otherwise it waits. It is paused, not refused: it goes as soon as there is room again, and only after <code>BackgroundMaxWaitSeconds</code> (ten minutes by default) does it get a 503 that names the setting.</p>

<p>A node that disconnects, is paused by its owner, is over its own resource limits or is cordoned is no longer one the coordinator can route to, so its places have already left the count. That is how a failure reaches the classes: no separate signal, just less room, and background work is what waits first.</p>

<h2>Two more rules</h2>

<p>When the whole fleet is full and requests wait for a place, a waiting request does not go ahead of a higher class waiting for the same model. And when the coordinator holds requests until a node frees a slot (the slot wait from 3.74), essential work is not held: it goes to the engine at once.</p>

<p>A request may lower its own class with the header <code>X-InferHub-Priority: background</code>, so a nightly job on an important key can step aside. It can never raise it. A misspelt class in the configuration stops the coordinator from starting, because otherwise it would quietly run as normal.</p>

<h2>Checked live</h2>

<p>A coordinator from source with three clients, one per class, and two nodes on a real Ollama, each running one request at a time:</p>

<ul>
<li>A long story from the normal client took one of the two places. The background request waited. An essential request was answered in 2.5 seconds while it waited. The background request went when the story finished.</li>
<li>One node was stopped. A background request, and a normal request that had lowered itself with the header, both waited. Essential and normal requests were answered at once on the remaining node.</li>
<li>When the node came back, both waiting requests went within about three seconds.</li>
<li>The audit trail from 3.85 recorded each request's class, and the metrics counted three background requests held and three released.</li>
</ul>

<p>The published 3.86.0 coordinator was then run the same way: with one node stopped, a background request waited while essential and normal ones were answered at once, and it went when the node came back.</p>

<ul></ul>
</ul>

<h2>What it does not do</h2>

<p>It never stops a running job to make room: a half-written answer thrown away is worse than a wait, so only new work waits. It covers chat and text generation, not embeddings, tools, speech, images or video. And a node that declares neither a concurrency cap nor slots gives the coordinator nothing to count, so nothing is held for it; every Ollama node declares slots, so on an ordinary fleet this does not come up.</p>

<p>Priority classes are off by default, and with them off nothing changes. The full notes are in the <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.86.0">v3.86.0 release</a>, and the documentation is at <a href="https://inferhub.devart.solutions/#idocs_priority">inferhub.devart.solutions</a>.</p>
