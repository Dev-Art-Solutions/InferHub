id: 6b055e28ef1fcb7d2e409430
slug: inferhub-3-112-several-requests-at-once-on-a-group
title: InferHub 3.112: a model split across machines now answers several people at once

<p>InferHub can split one model across several machines: a head runs it, and each member holds some of its layers. Every token visits each member in turn. A question we had not measured until now: what happens when four people ask at the same time?</p>
<p>llama.cpp, the engine a group runs, can put several requests into the same pass across the members. We measured how much that buys. We also found that the coordinator did not know the group could do it. 3.112 fixes that.</p>
<pre><code>"ShardGroups": {
  "llama-8b-split": {
    "Head": "shard-a", "Members": ["shard-b", "shard-c"],
    "Model": "llama31-8b.gguf", "Slots": 8
  }
}</code></pre>

<h2>What it does</h2>
<ul>
<li><strong>The head says how many requests the group runs at once</strong>, as the group&rsquo;s own engine reports it. Before, the coordinator used the count of the head&rsquo;s own, separate engine. For a default Ollama that count is one, so with the coordinator&rsquo;s slot wait on, a group got one request at a time.</li>
<li><strong><code>Slots</code> sets the count</strong> when you want something other than llama.cpp&rsquo;s own choice (four). Every request can still use the group&rsquo;s whole context. Without the extra flag the head now passes, a set count would have cut the context into equal parts: 4096 tokens became 1024 per request when we measured it.</li>
<li><strong>The group&rsquo;s status shows the count</strong>, and nothing changes for a group you do not touch.</li>
</ul>

<h2>Measured first</h2>
<p>An 8B model split over three containers on one PC, CPU only. We sent one, two or four requests at the same moment, each for 48 tokens at temperature 0, and counted tokens per second across all of them. Two runs of each.</p>
<table>
<thead><tr><th>Round trip</th><th>One request</th><th>Two at once</th><th>Four at once</th></tr></thead>
<tbody>
<tr><td>0&nbsp;ms</td><td>6.11 tokens/s</td><td>9.88 (1.62&times;)</td><td><strong>13.56 (2.22&times;)</strong></td></tr>
<tr><td>30&nbsp;ms</td><td>2.77</td><td>4.58 (1.66&times;)</td><td>4.96 (1.79&times;)</td></tr>
<tr><td>120&nbsp;ms</td><td>0.60</td><td>0.98 (1.63&times;)</td><td><strong>1.42 (2.35&times;)</strong></td></tr>
</tbody>
</table>
<p>Each request got the same text it gets alone. With the engine limited to one request at a time, the same bursts stayed at one request&rsquo;s speed: four requests at 120&nbsp;ms finished after 319 seconds instead of 132.</p>
<p>Then through InferHub itself, with the coordinator&rsquo;s slot wait at 20 seconds and four chats sent together. On 3.111 the four took 28.5 seconds, and their first words arrived at 0.2, 7.9, 15.7 and 20.3 seconds. On 3.112 they took about 15 seconds, and every first word arrived within 3.2 seconds. The text was the same. The published 3.112 images gave the same result.</p>

<h2>What it costs</h2>
<ul>
<li><strong>Each request is slower than it would be alone.</strong> In a burst of four at 0&nbsp;ms, each got 3.9 tokens per second instead of 6.1. More slots trade one person&rsquo;s speed for everyone&rsquo;s.</li>
<li><strong>The network is not paid once for all of them.</strong> At 30&nbsp;ms a pass of four cost 0.75 seconds against 0.33 for one, which is more than the extra computing explains. Why is not yet established.</li>
</ul>

<h2>What we have not shown</h2>
<ul>
<li><strong>The same text, always.</strong> With two slots and four requests, one run gave two of the requests a different answer at temperature 0. llama.cpp does not promise identical numbers when requests are batched differently. Every run with four slots kept the text.</li>
<li><strong>Graphics cards, a real network and bigger models.</strong> Our lab adds delay and nothing else. A 70B with several slots needs memory on the head for every request&rsquo;s context, and that is not measured.</li>
<li><strong>Several slots together with the draft model from 3.111.</strong> llama.cpp runs both; we have not measured them together.</li>
</ul>
<p>The scripts and every run&rsquo;s files are in <code>deploy/lab/shards/</code> and <code>docs/research/runs/2026-11-24-*</code> in the repository.</p>
