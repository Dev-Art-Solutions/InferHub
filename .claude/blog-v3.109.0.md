slug: inferhub-3-109-the-coordinator-places-a-group
title: InferHub 3.109: the coordinator picks the machines for a split model itself

<p>InferHub can split one model across several machines: a head runs it, and each member holds some of its layers. Until this release an admin chose those machines by hand: which one is the head, which ones are members, which ones stand in when a member leaves. The coordinator already knew most of what that person had to look up: how much memory each machine offers, and how long a round trip takes between any two of them. The console has drawn that matrix since 3.95.</p>
<p>The one thing it did not know was which machine holds which model file. Machines now report that, so a group can name only its model:</p>
<pre><code>"ShardGroups": {
  "llama-8b-auto": { "Mode": "auto", "Model": "llama31-8b.gguf", "Context": 2048 }
}</code></pre>

<h2>How it chooses</h2>
<ul>
<li><strong>The head</strong> is a machine that holds the file and whose owner has allowed it to head a group.</li>
<li><strong>The members</strong> are machines the head can reach whose memory, together with the head&rsquo;s, holds the model at that context. Among those, the coordinator takes the set with the <strong>smallest sum of round trips from the head</strong>. Every token goes to each member and back in turn, so those round trips add up. They set a group&rsquo;s speed more than bandwidth does. After that it prefers fewer members, and then members that hold the file themselves, because those load their share from their own disk (3.108).</li>
<li><strong>A placement is kept while it can run.</strong> A slightly better round trip never reloads a running group, because a reload takes minutes. When a machine in the group cannot take part (it left, was cordoned, or its owner paused it), the group is placed again, and the status says why.</li>
<li><strong>It waits for the fleet to hold still</strong> for 30 seconds before placing, so machines that are still connecting are not left out.</li>
</ul>
<p>The group&rsquo;s status explains the choice:</p>
<pre><code>"placement": [
  "head shard-a: holds llama31-8b.gguf (4.3 GiB), offers 3 GiB",
  "members shard-d (0.30 ms, offers 3 GiB, holds the file): 0.30 ms of round trips per pass, about 5.6 GiB needed of 6 GiB offered",
  "left out: shard-b: 20 ms from shard-a, not needed",
  "left out: shard-c: 21 ms from shard-a, not needed"
]</code></pre>

<h2>What we measured</h2>
<p>An 8B model, four machines in Docker on one PC, each offering 3&nbsp;GiB. Two of them were put 20&nbsp;ms away with a simulated network delay.</p>
<table>
<thead><tr><th></th><th>Placed on</th><th>Round trip</th><th>64 tokens</th></tr></thead>
<tbody>
<tr><td>All four up</td><td>the near machine</td><td>0.30&nbsp;ms</td><td>9.9&nbsp;s</td></tr>
<tr><td>The near machine stopped</td><td>a far one, running 52&nbsp;s later</td><td>20&nbsp;ms</td><td>11.8&ndash;12.3&nbsp;s</td></tr>
</tbody>
</table>
<p>The text was the same every time, wherever the group was placed.</p>

<h2>What this does not show</h2>
<p>It does not show a real network: every round trip here was simulated on one PC. It compares one near and one far placement, not every choice the coordinator can make, so it does not show that the sum of round trips predicts speed in general. It does not show more than four machines, GPUs, or a 70B placed this way.</p>
<p>Next on this track: a request that failed before its first token is retried once the group has been placed again.</p>
