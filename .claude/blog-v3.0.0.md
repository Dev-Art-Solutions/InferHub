# Blog post — InferHub v3.0.0

**Status: DRAFTED, NOT POSTED.** Publish only once **`v3.0.1`** is tagged and its GHCR images are
live — v3.0.0 shipped with the concurrent-bootstrap bug the post itself describes, so `3.0.1` is the
version being announced. The connector is insert-only with a unique slug and no update or delete — so call
`create_post` **once**, already visible (`isVisible_en=true`, `isVisible_bg=false`); a hidden
draft locks the slug and cannot be flipped. Confirm the slug is absent via `list_posts` first.
Content HTML is passed **entity-escaped** — that is how every prior post is stored; do not "fix" it.

- **slug:** `inferhub-3-0-warm-standby`
- **isVisible_en:** `true`  ·  **isVisible_bg:** `false` (one shot — do not create hidden first)
- **author:** `Admin`
- **title_en:** InferHub 3.0: the hub stops being the single point of failure
- **title_bg:** InferHub 3.0: хъбът вече не е единствената точка на отказ
- **excerpt_en:** A second coordinator can now run as a warm standby over the same Postgres — the primary dies, the standby takes the lease, your nodes reconnect on their own, and the mesh keeps serving. Plus the two bugs that only showed up when we pulled the plug for real, one of them after tagging.
- **excerpt_bg:** Втори координатор вече може да работи като топъл резервен над същия Postgres — първичният отпада, резервният поема лиценза, възлите се преизключват сами и мрежата продължава да обслужва. Плюс bug-ът със split-brain, който се показа чак когато наистина дръпнахме кабела.

---

## content_en (HTML)

<p>InferHub has always been honest about one thing: the coordinator is the durability anchor. Nodes are disposable — they come and go, they live behind NAT, they reach out rather than being reached, and losing one costs you a retry. The hub was the opposite. It held the fleet view, the routing, the affinity map. Losing it lost everything.</p>

<p>That was a deliberate trade for a long time, and it bought a lot of simplicity. It also meant that anyone running InferHub as real infrastructure eventually asked the same question, and the honest answer was "you can't, yet."</p>

<p>3.0 is the answer. You can now run a second coordinator as a <strong>warm standby</strong>.</p>

<h2>How it works, in one paragraph</h2>

<p>Two coordinators share one Postgres. A single row acts as a lease: whoever holds it is <strong>active</strong> and serves inference, and the other runs <strong>standby</strong> — it answers <code>/health</code>, <code>/api/status</code>, <code>/metrics</code> and the admin API, but returns <code>503</code> on every inference route. Your nodes are configured with both addresses and walk the list until one accepts them. When the active hub dies, the standby takes the lease within the TTL, the nodes reconnect to it on their own, and the mesh serves again.</p>

<p>No manual promotion. No migration step. Nothing to run.</p>

<h2>Why there is nothing to migrate</h2>

<p>This is the part that makes the whole feature small instead of enormous, and it is worth pulling apart.</p>

<p>Under the <code>postgres</code> provider, everything durable already lives outside any one coordinator: the vector store and the usage ledger are rows in a database both hubs can reach. Everything <em>else</em> a coordinator holds — the node registry, the metrics, the audit log, the affinity map — is <strong>derived</strong>. It was never authoritative. It rebuilds itself as nodes reconnect, which is exactly what a restart has always done.</p>

<p>So a promoted standby doesn't need to be handed anything. It reads the same rows the old primary was reading and learns the fleet from the nodes as they arrive. There is no cross-hub gossip, no state replication, no consensus protocol. There is one database, as before, and now two interchangeable readers of it.</p>

<p>Which is also why this is Postgres-only. Under the <code>local</code> provider the vector store's raw file is per-hub, and clustering that is a different, larger problem. Saying so plainly is better than shipping a config flag that quietly produces two sources of truth.</p>

<h2>The lease is a row, not a lock</h2>

<p>Postgres has advisory locks, and reaching for one was the obvious move. We didn't, for two reasons.</p>

<p>An advisory lock is scoped to a <em>session</em>. A pooled connection dropping quietly releases leadership, with nothing anywhere to observe — you find out because two hubs are serving. And a lock carries no expiry and no counter, so a coordinator that has been cut off from the network has nothing local to reason about. It can only ask, and asking is the thing it can't do.</p>

<p>A row with an expiry and an acquisition counter can be read, logged, graphed and argued with by both sides. Taking or renewing it is one conditional upsert — <em>update if it's already mine or the other one's claim has lapsed</em> — decided entirely by the database's own clock, so there is no read-then-write window for two coordinators to both walk through. And it cost no new dependency: the driver was already there for the vector store.</p>

<h2>The interesting part: fencing</h2>

<p>Failover is the easy half. The hard half is making sure there is never a moment when <em>both</em> hubs believe they are in charge.</p>

<p>The awkward case isn't a hub dying — a dead hub serves nobody. It's a hub that is perfectly alive and healthy but has been cut off from the database. It still has your nodes connected. It will still happily route your requests. And it cannot be <em>told</em> it lost the lease, because by definition it can't reach the thing that knows.</p>

<p>So the rule can't be "demote when told to." It has to be: <strong>demote when I have not proved I lead, within the TTL, by my own clock.</strong> That deadline is the same one Postgres uses to hand the lease to the standby, so the two windows can't overlap.</p>

<p>There's a real cost here, and it should be said out loud rather than buried: if the database becomes unreachable, the mesh stops serving even though the GPUs are fine. That's the correct trade. A request the mesh cannot attribute to a single leader is worse than a <code>503</code> your load balancer routes elsewhere.</p>

<h2>The bug that only appeared when we pulled the plug</h2>

<p>All of the above was implemented, unit-tested, and green. Then we brought the two-coordinator stack up on the actual built images and stopped the Postgres container underneath the active hub, to watch the fence do its thing.</p>

<p>It demoted at <strong>23 seconds</strong>. The lease TTL was 15.</p>

<p>The reason is almost funny. When the deadline check lives <em>after</em> the renewal attempt, and the renewal attempt is a database connection to a host that has gone away, the attempt sits there burning the driver's connect timeout before it fails. The fence was being outrun by its own health check.</p>

<p>Eight seconds doesn't sound like much. But the lease row frees at 15 seconds, so for those eight the standby had already taken over and was serving, while the old primary still believed it was in charge — with your nodes attached to it. That is precisely the split brain the fence exists to prevent, sitting inside the fence.</p>

<p>The fix is small: check the deadline <em>before</em> doing any I/O, bound the attempt by whatever is left of it, and clamp the loop's sleep so tick granularity can't add slack either. Re-measured, it fences at 15 seconds — exactly the number the documentation promises.</p>

<p>No test we would have thought to write would have caught this. It needed a real database, really unreachable, on a real clock. Which is the same lesson this project keeps re-learning: <a href="https://github.com/Dev-Art-Solutions/InferHub">a green suite tells you the code does what you told it to</a>, not what happens when the world stops cooperating.</p>

<p>The same rehearsal turned up a second one, smaller but just as characteristic. <code>CREATE SCHEMA IF NOT EXISTS</code> is not atomic — two sessions racing it can both pass the existence check, and one dies on the unique index. Everywhere else in InferHub, bootstrap happens once, on one hub, so the race had never been reachable. Here two coordinators booting at the same instant is the <em>normal</em> case. An HA pair that crashes half of itself on a cold boot is not, whatever else it might be, HA.</p>

<h2>And then it happened again, after tagging</h2>

<p>I fixed that race where I found it — in the lease — and wrote a note to myself, in the design doc, in as many words: <em>if the vector store or the usage ledger ever bootstrap concurrently, they need the same treatment.</em> Then I tagged 3.0.0.</p>

<p>The release ritual here ends with a rule earned the hard way: pull the <em>published</em> images and run the feature, because a green test suite tells you nothing about what actually shipped. So I pulled 3.0.0 from the registry, brought two hubs up against an empty database, and watched one of them exit on <code>pg_extension_name_index</code> while the other came up perfectly happy.</p>

<p>Same race. Two layers up. In the exact two places the note named.</p>

<p>What makes it worth writing about isn't the SQL — it's the failure mode. The surviving hub took the lease and served normally, so the mesh <em>looked</em> fine. It was just silently down to one coordinator, with no standby, which is to say: the high-availability feature had quietly turned itself off. And the error message blamed a missing database privilege, so the first hour of debugging would have been spent asking a DBA about permissions that were never the problem.</p>

<p>3.0.1 fixes it properly, in one place, for all three bootstraps, with a test that races eight coordinators and fails without the fix. <strong>Pull <code>3.0.1</code>, not <code>3.0.0</code>.</strong></p>

<p>The lesson I'd actually like to keep: a hazard you have written down but have not fixed is still shipped. Documenting a risk feels like progress and isn't. The note was correct, specific, and sitting in the file the whole time.</p>

<h2>What a client and a load balancer see</h2>

<p>The hub does not become a load balancer. That job belongs to whatever you already run — nginx, HAProxy, an ELB, DNS failover. What InferHub owes it is honest signals, and there are three: every response carries <code>X-InferHub-Role: active</code> or <code>standby</code>, <code>/health</code> gains a <code>role</code> field, and inference against a standby returns <code>503</code> with <code>Retry-After</code> in the caller's own dialect — the OpenAI error envelope on <code>/v1</code>, so an SDK surfaces a sentence instead of "unknown error".</p>

<p>One deliberate choice worth flagging, because it looks wrong at first: <strong><code>/health</code> still returns <code>200</code> on a standby.</strong> A standby <em>is</em> healthy. It is simply not leading. If it reported itself unhealthy, every orchestrator on earth would restart-loop the exact instance whose entire job is to sit quietly and wait. Drain on the role, or on the inference <code>503</code> — not on <code>/health</code>.</p>

<h2>What 3.0 is not</h2>

<p>It is a major version because the hub is now survivable, not because the clustering track is finished. Two things are explicitly still to come, and calling them out is more useful than implying them:</p>

<ul>
<li><strong>Active-active.</strong> One hub serves at a time. This is failover, not load sharing.</li>
<li><strong>Clustering the <code>local</code> vector provider.</strong> HA needs Postgres, for the reason above.</li>
</ul>

<p>Everything is off by default. If you run one coordinator — which is most people, and entirely reasonable — 3.0 behaves byte-identically to 2.13. No lease, no database connection, no role header, no new keys in your status JSON.</p>

<p>A two-coordinator compose overlay with an nginx front and a failover runbook ships in <code>deploy/docker/</code>, so you can rehearse the whole thing on your own machine in about five minutes. I'd recommend doing exactly that before you need it — it's how we found the fencing bug.</p>

<p>Self-hosted, MIT, .NET 10. Still zero new dependencies: the lease is one Postgres row and the driver was already in the box.</p>

<p>👉 <a href="https://github.com/Dev-Art-Solutions/InferHub">github.com/Dev-Art-Solutions/InferHub</a></p>
