slug: inferhub-3-110-a-request-that-survives-a-member-leaving
title: InferHub 3.110: a request that survives a machine leaving a split model

<p>InferHub can split one model across several machines: a head runs it, and each member holds some of its layers. Since 3.97 a group survives a member leaving: a spare machine stands in, the head loads the model again, and the group answers a minute later. But the request that was running when the member left did not survive. When a member disappears, llama.cpp stops the head with it, so that request failed with the engine&rsquo;s error. Every request sent during the next minute got a 503 that said the group was reassembling.</p>
<p>That request had not given the caller anything yet, so sending it again cannot duplicate anything. One setting now does that:</p>
<pre><code>"ShardGroups": {
  "llama-8b-split": {
    "Head": "shard-a", "Members": ["shard-b", "shard-c"], "Spares": ["shard-d"],
    "Model": "llama31-8b.gguf", "RetrySeconds": 180
  }
}</code></pre>

<h2>What it does</h2>
<ul>
<li><strong>A request that failed before the caller received anything waits at the coordinator</strong> until the group runs again, and is then sent again, for up to <code>RetrySeconds</code>. A non-streaming request is retried whole. A stream is retried only if its very first chunk was the error.</li>
<li><strong>It does not retry what it should not.</strong> A refusal (a 4xx) is about the request, not the group. A stream that has already sent text would get a second, different answer attached to the first.</li>
<li><strong>It does not retry too early.</strong> A missing member stops the head within seconds, sometimes before the coordinator has heard that the member left, so the group can still look healthy for a moment. The retry waits until the coordinator has seen the group stop and start again, or five seconds have passed.</li>
<li><strong>A request that arrives while the group reassembles waits too</strong>, instead of getting the 503.</li>
<li><strong>A member that comes back takes its place back from the spare, once it has held still.</strong> Reading the code for this release, we found that a member used to take its place back the moment it could, so a machine that dropped off and came back caused two reloads, each one minutes long on a big model. Now the spare keeps the place for 30 seconds after the member returns, and the group&rsquo;s status says so: <em>shard-d stands in for shard-c, which is back: it takes its place back in 29 s</em>.</li>
<li><strong>A group the coordinator places itself</strong> (3.109) can opt in with <code>"FailBack": true</code>: when a machine it lost is back, the group is placed again.</li>
</ul>
<p><code>RetrySeconds</code> is 0 by default, and then nothing changes.</p>

<h2>What we measured</h2>
<p>An 8B model over four machines in Docker on one PC: a head, two members and a spare, each offering 3&nbsp;GiB, CPU only.</p>
<table>
<thead><tr><th>Moment</th><th>What the client got</th></tr></thead>
<tbody>
<tr><td>A 128-token request on its own</td><td>200 in 20.8&nbsp;s</td></tr>
<tr><td>The same request, one member&rsquo;s container killed 6&nbsp;s into it</td><td>200 in 41.3&nbsp;s, the same text, answered with the spare in the group</td></tr>
<tr><td>A short request sent while the group reassembled</td><td>200 after 9.5&nbsp;s (before this release: a 503)</td></tr>
<tr><td>The killed member started again</td><td>back in its place 30&nbsp;s after it returned</td></tr>
<tr><td>A short request sent during that reload</td><td>200 after 11.6&nbsp;s</td></tr>
</tbody>
</table>
<p>The text was the same before the member left, with the spare, and with the member back.</p>

<h2>What this does not show</h2>
<p>It does not show a real network or a real failure. A killed container closes its connections at once, so the coordinator knew within milliseconds. A pulled cable or a laptop going to sleep leaves the coordinator waiting for the connection to time out, and that wait counts against <code>RetrySeconds</code>. It does not show a 70B: the 8B reloaded in about 15 seconds, and a 70B takes minutes, so the setting has to fit the model. Community groups still pick the machines that offer the most memory each time, so a larger machine arriving still moves one of their members. GPUs were not used.</p>
<p>Next on this track: fewer round trips across the internet by running a small draft model on the head. It ships only if it is 1.25&times; faster on every prompt we measure.</p>
