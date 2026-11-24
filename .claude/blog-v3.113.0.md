id: 6b060a7068fc43092c4fe3f2
slug: inferhub-3-113-who-gave-how-much-to-groups
title: InferHub 3.113: who gave how much to a model split across machines

<p>InferHub can split one model across several machines: a head runs it, and each member holds some of its layers. The members&rsquo; owners set memory aside for this, and every answer the group gives uses their computers. Until now nobody could see how much each machine gave. Once a member left, or a spare had stood in for an hour, its part disappeared from every view.</p>
<p>3.113 makes it visible. The coordinator keeps a count per machine, and the console shows it in a new table, <em>Contribution to groups</em>. It is only shown. Nothing is enforced: there are no credits, and nobody has to give in order to use.</p>
<pre><code>{ "name": "shard-c", "connected": false, "seconds": 70, "mibHours": 59.7,
  "requests": 6, "promptTokens": 108, "generatedTokens": 256,
  "shareTokens": 113.8, "groupsNow": [] }</code></pre>

<h2>What it counts</h2>
<ul>
<li><strong>Memory.</strong> The memory a machine&rsquo;s owner set aside for groups, for as long as a running group used that machine. The whole amount counts, because it is kept free for groups whether a group fills it or not.</li>
<li><strong>Tokens through it.</strong> Every word of an answer passes through every machine in the group, so each answer counts for all of them.</li>
<li><strong>Its share.</strong> A machine holding 10 of the model&rsquo;s layers did less of the work than one holding 20. The share is the tokens times the machine&rsquo;s part of the layers.</li>
<li><strong>A machine that left keeps its row</strong>, marked as gone. The count lives in the coordinator&rsquo;s memory and starts again when the coordinator restarts. The page says from when.</li>
</ul>

<h2>In the lab</h2>
<p>An 8B model split over a head and two members on one PC, with a fourth machine waiting as a spare. The model&rsquo;s layers were divided 11 : 11 : 10.</p>
<table>
<thead><tr><th>Answers &middot; tokens &middot; share</th><th>Head</th><th>Member b</th><th>Member c</th><th>Spare d</th></tr></thead>
<tbody>
<tr><td>After 6 answers</td><td>6 &middot; 364 &middot; 125.1</td><td>6 &middot; 364 &middot; 125.1</td><td>6 &middot; 364 &middot; 113.8</td><td>0</td></tr>
<tr><td>Member c stopped, the spare stood in, 4 more answers</td><td>10 &middot; 560 &middot; 192.5</td><td>10 &middot; 560 &middot; 192.5</td><td><strong>6 &middot; 364 &middot; 113.8, gone</strong></td><td><strong>4 &middot; 196 &middot; 61.2</strong></td></tr>
</tbody>
</table>
<p>The member that stopped kept what it had given. The spare started at zero and was credited only for the answers after it joined. Every chat answered in about 5 seconds.</p>

<h2>What we have not shown</h2>
<ul>
<li><strong>Machines of different owners</strong>, which is what this count is for. Our lab is one PC with one owner.</li>
<li><strong>Numbers that survive a restart.</strong> They start again on purpose. A permanent record would belong in the audit trail.</li>
<li><strong>A draft model</strong> (from 3.111) runs on the head&rsquo;s own processor and is not counted as anyone&rsquo;s share. In a cluster, each coordinator counts only what passed through it.</li>
</ul>
<p>The lab script and the three readings are in <code>deploy/lab/shards/contribution-lab.sh</code> and <code>docs/research/runs/2026-11-25-contribution-source/</code> in the repository.</p>
