<h2>The one ceiling a coordinator can't even see</h2>

<p>Every limit a node has ever had — how many concurrent requests, how much VRAM, which tools it
may run — is something a hub-sent profile can narrow further, because the node's own configuration
was always the outer bound anyway. v3.47 adds a first: a ceiling with no field on that profile at
all. <code>Node:ResourceLimits</code> is a purely local cap on how much of a box's own CPU and GPU it
lets itself be routed to consume, and a coordinator — trusted or compromised — cannot set it, raise
it, or read it back.</p>

<p>It exists because the answer to "can the hub make my box do less" has always been yes, and the
answer to "can I make my own box do less, no matter what the hub asks" had always quietly been
"only by unplugging it." Now it isn't.</p>

<h3>Soft first</h3>

<p>Set <code>Node:ResourceLimits:MaxCpuPercent</code> and/or <code>MaxGpuPercent</code> (1-100, both
off by default) and the node starts sampling its own usage every few seconds — hand-rolled per
platform rather than a new dependency: <code>GetSystemTimes</code> on Windows,
<code>/proc/stat</code> on Linux. A handful of consecutive polls over either cap and the node
withdraws itself from new placement. A meshed node does this exactly the way it already announces an
unhealthy backend — it still reports its models, it just stops being a routing candidate — and a
standalone node answers a new request with the same <code>503</code> and <code>Retry-After</code> its
concurrency gate has always used. Nothing already running is touched; this decides admission, not
what happens to work in flight. It clears itself the same way, after enough clean readings.</p>

<pre><code>$ curl -i -X POST http://localhost:5099/api/chat -d '{"model":"llama3.2","messages":[...]}'
HTTP/1.1 503 Service Unavailable
Retry-After: 15

{"error":"node is over its own configured resource cap: CPU 95% &gt; Node:ResourceLimits:MaxCpuPercent (80%); retry in 15s"}
</code></pre>

<h3>Then hard, where the platform allows it</h3>

<p><code>HardCpuCapPercent</code> is a second, independent mechanism: a real Windows Job Object CPU
rate limit, applied to the node's own tool-worker child processes — the transcription, the speech,
the image and video workers, anything that spawns as a subprocess. It's Windows-only today, and it
needs a restart, unlike the two percentages above it: re-configuring a job object's rate after
processes are already running inside it isn't something this release attempts.</p>

<h3>A configuring page the node serves itself</h3>

<p>Rather than a separate desktop program, the node's existing local API grows one small page:
<code>GET /api/admin/resource-limits</code>. It shows live CPU/GPU readings and the current throttle
state, and a save reaches the running node within one poll interval — no restart. It writes to an
optional file beside the node's own configuration rather than into it, so nothing programmatic ever
touches the hand-authored <code>appsettings.json</code>. The page inherits the same loopback guard
every other route on that host already has, for free.</p>

<h3>Verified live</h3>

<p>A solo node run from source with the cap set to an unreachable 1% tripped within two polls,
answered a real chat request with the 503 above, and — with no restart — accepted a raised cap
through the admin page and passed the very next request. The write landed exactly where the page
itself reads from; getting the two to agree on one file, rather than two files nobody noticed
disagreed, was the one thing this release found live rather than by unit test.</p>

<p>What's not yet verified: a real two-node mesh actually rerouting a request away from a throttled
node, rather than only refusing it in isolation — the unit suite covers the routing narrowing
directly, but nobody has watched a live fleet do it end to end. Named here rather than implied.</p>
