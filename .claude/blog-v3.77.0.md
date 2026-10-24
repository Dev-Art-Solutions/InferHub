slug: inferhub-3-77-sharing-on-the-owners-terms
title: InferHub 3.77: your GPU at night, not while you work, and two hours a day at most
excerpt: A node in a network of friends' PCs can now say when it shares and how much. Running it found the hub had been telling everyone a paused node had a broken inference server.

<p>InferHub 3.76 made a node a machine of its own. Its owner can set it to share with the network always, only when they are not using it, or not at all. In a network of friends' PCs, the next two questions came straight away: <em>use my GPU at night, not while I work</em>, and <em>no more than two hours a day</em>. Since 3.77 both are settings on the node.</p>

<pre><code class="json">"Node": { "Sharing": {
  "Hours": ["mon-fri 18:00-08:00", "weekends all day"],
  "TimeZone": "Europe/Sofia",
  "DailyNetworkMinutes": 120 } }</code></pre>

<h2>Sharing hours</h2>

<p>Each line is days, then hours, written the way you would say them: <code>mon-fri 18:00-08:00</code>, <code>sat,sun all day</code>, <code>daily 22:00-07:00</code>. Two rules make lines like these mean what their owner meant:</p>

<ul>
<li><strong>A window belongs to the day it starts on.</strong> <code>fri 22:00-06:00</code> is Friday night into Saturday morning, not Saturday's early hours.</li>
<li><strong>Windows that touch are one.</strong> <code>daily 22:00-24:00</code> and <code>daily 00:00-07:00</code> do not close for an instant at midnight.</li>
</ul>

<p>The times are in the owner's zone, not the server's. When the clock jumps forward in spring, a window that would start in the missing hour opens at the first minute the clock actually shows. A line the node cannot read stops it from starting, and the control API refuses it with the reason. A node does not get to share at hours nobody wrote.</p>

<h2>A daily allowance</h2>

<p><code>DailyNetworkMinutes</code> is how many minutes of other people's work the node does in a day. Each job counts on its own, so two at once use the minutes twice as fast. Your own work through the node's local API never counts. The minutes renew at midnight, and the day's total is written to disk after every network job. Restarting the node is not a way to give the network a second day.</p>

<h2>What happens when they run out</h2>

<p>Nothing new, which is the point. Outside its hours, or once the minutes are used, the node pauses exactly the way 3.76's <code>paused</code> does. It stays connected, its models stay visible, and the hub sends it nothing new. A job already running finishes, because someone else's half-written answer is not the owner's to throw away. The pause now carries a reason and the time it lifts:</p>

<pre><code class="json">"sharing": {
  "paused": true,
  "reason": "outside its sharing hours; shares again Mon 09:00",
  "resumesAtUtc": "2026-10-26T07:00:00+00:00",
  "allowance": { "minutes": 120, "usedSeconds": 60.7, "leftSeconds": 7139.3 }
}</code></pre>

<p>That Monday 09:00 is 07:00 UTC, not 06:00, because Sofia leaves summer time on the Sunday in between. The node worked that out itself. Because it is the same pause, a 3.76 hub already holds it. The hub's console now shows it too, as a <em>not sharing · until Mon 09:00</em> pill.</p>

<h2>What running it found</h2>

<p>We ran a real hub and node from source against the machine's Ollama. We closed the node's hours, then asked the hub for a chat. The answer was:</p>

<pre><code>503  every node holding model 'qwen2.5:0.5b' reports an unhealthy inference backend</code></pre>

<p>The backend was fine. The hub groups every node it cannot use right now together: a broken backend, a box over its own resource cap, and, since 3.76, a node its owner paused. Its message named only the first. With sharing hours, that would have been every network's answer every night, and it sends an operator to fix a server that is not broken. In 3.77 the hub names the cause and says when to come back:</p>

<pre><code>503  Retry-After: 2716
every node holding model 'qwen2.5:0.5b' is not sharing now (live116-node: today's network allowance of 1 min is used; shares again tomorrow 00:00)</code></pre>

<p>The rest held. Four network chats at once took 9, 15, 15 and 21 seconds. The node counted 60.7 seconds against a one-minute allowance and paused. A local chat on the same node counted nothing. We then killed the node and started it again: it came back paused, and the hub's <code>Retry-After</code> was the time left until midnight.</p>

<h2>In the app</h2>

<p><a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/desktop/v0.2.0">InferHub Desktop 0.2.0</a> edits the hours, the zone and the minutes on its Overview tab, and shows how much of today's allowance the network has used. The node checks the lines and the app shows the node's answer. The packaged app, with its own 3.77 node, was run before this post: the terms took effect, survived a restart, and the pause showed with its reason.</p>

<h2>What we did not establish</h2>

<ul>
<li>No node was left running across midnight or across a clock change. Both are covered by tests with a manual clock and by the resume time the live node reported.</li>
<li>The console pill was checked against the status contract, not looked at in a browser.</li>
<li>The desktop app on Linux and macOS is still built by CI only.</li>
</ul>

<p>With no hours and no allowance, a node behaves exactly as 3.76. <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.77.0">Release notes</a> · <a href="https://inferhub.devart.solutions/#idocs_sharing_terms">documentation</a>.</p>
