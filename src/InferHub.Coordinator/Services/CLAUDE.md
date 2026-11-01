# InferHub.Coordinator/Services — agent context

**Scope: `src/InferHub.Coordinator/Services/`.** Where a request goes, how long it may wait for a
place, and how long it may run there: the router's tiers, the request queue's two waits, the
dispatch deadlines. **Split out of `src/InferHub.Coordinator/CLAUDE.md` in phase 110**, which was at
its 1100-line budget — 62 D6, 67 D6, 69 D8 and 81 a fifth time, with phases 86 and 89 moved
**unedited**.

> **Read the root `CLAUDE.md` first**, then `src/InferHub.Coordinator/CLAUDE.md` — its phase 25 D5
> (the bounded queue and `FleetSaturation`) and phase 26 D4/D5 (measured routing) are the ground
> everything here stands on, and they stay there.

## Related context

- The relay a sealed node fetches through (`NodeRelayEndpoints.cs`, `Relay:`) and its cache (`RelayCache.cs`, `Relay:Cache:`) and its supply (`RelaySupply*.cs`, `SupplyJobs.cs`, `Relay:Supply:`): `src/InferHub.Node/Control/CLAUDE.md`, phase 122 D3, phase 123 and phase 124
- Everything else on this host: `src/InferHub.Coordinator/CLAUDE.md`
- What a node declares about its slots (108 D1–D3) and how an engine is asked (109): `src/InferHub.Node/CLAUDE.md`, `src/InferHub.Node/Backends/CLAUDE.md`

## Decisions recorded here

### Phase 86 (the router prefers a node whose card needs no switch) — the node's half is in `src/InferHub.Node/CLAUDE.md`, phase 85's block

`Heartbeat.OnDemand` (`OnDemandState`: holder, `warmFor` capability kinds, switching, waiting) is
stored on `NodeRegistryEntry` and handed to the router **on `RoutableNode`** — `FindNodesWithModel`
keeps its signature (70 D7's cost: no fifth boolean).

**D3, load-bearing — three tiers ahead of the strategy, and a preference, never a filter.**
`Router.WarmestTier` splits the candidates into *warm* (no state, or `warmFor` has the capability
and not switching), *free* (a state with a null holder), *cold* (another tenant holds the card, or it
is mid-switch), and the existing least-busy/throughput pick plus affinity run over the best
non-empty tier. **No state on any candidate is one tier, which is the identity** — that is the
byte-identity argument and `NoOnDemandStateMeansOneTierAndTodaysPick` is named after it. A request
with no capability is not tiered ("warm for what"). A cold node that is the only holder is still
returned and waits in its own arbiter exactly as in v3.50. Affinity only holds inside the chosen tier:
a conversation's KV cache is worth less than a weight reload. *Rejected:* a weighted load + switch
penalty score — it needs a switch-cost number nobody has measured (48 D1's "usually right" trap).

**D2 — the hub never learns what a tenant is.** The node resolves `tool:diffusion` → `image`/`video`
and sends the kinds; the holder string is only displayed. **The hub never commands the card**
(no prefetch, no evict) — `Node:OnDemand` is the operator's, like 82's cap.

**D5 — visibility.** `/api/status` `onDemand` verbatim (null for a node that does not run on demand);
console pill `gpu <holder>` / `gpu free` / `gpu switching`, neutral not a warning;
`inferhub_node_gpu_holder{node,holder}` = 1, absent for no-state nodes, `holder` bounded by the node's
manifests plus the reserved `none`/`switching`. `Touch` raises `Changed` on holder/switching
transitions only, not on the waiter count.

Tests: `OnDemandRoutingTests` (Coordinator), `PrometheusMetricsTests.OnlyAnOnDemandNodeEmitsAGpuHolderSeries`,
`OnDemandRoutingMeshTests` (Mesh — real SignalR; the pre-3.51 five-field heartbeat routes as before).

### Phase 89 (`Dispatcher:Deadlines` — one dispatch deadline per capability)

**D1, load-bearing — keyed by the capability the job was routed on, empty by default.**
`DispatcherOptions.DeadlineFor` looks the capability up case-insensitively and falls back to
`TimeoutSeconds` (still clamped to 1, never refused); nothing ships in `Deadlines`, so a deployment
that sets nothing times every job exactly as v3.53 did (`TheShippedAppsettingsDeclaresNoDeadline`
reads the real file). *Rejected:* shipping `video: 3600` as a default — right number, but it changes
the behaviour of every hub that upgrades without reading the notes; the appsettings comment gives the
figures instead. *Rejected:* reading the node manifest's `requestTimeoutSeconds` — the hub would be
trusting a node for how long the hub waits, and a mixed fleet would give one capability two deadlines.

**D2 — inference jobs use the router's own reading, `CapabilityKinds.ForJobKind`.** `generate` is
`chat`; the hub's internal kinds (`vector-query`, corpus jobs) have no capability and always take
`TimeoutSeconds`, so a `vector-query` key is not a hidden knob.

**D3 — the expiry names its clock.** `DispatchDeadline.Expired()` says the capability, the seconds
and the key (`Dispatcher:Deadlines:video` or `Dispatcher:TimeoutSeconds`); one warning log line with
the same three. It reaches the caller wherever the old text did (a failed image/video job's
`error.message`, a native stream's error chunk). The blocking chat 504 keeps its fixed text.

**D4 — any capability name is a valid key; a value below 1 fails startup.** Custom tools declare
their own capabilities (40 D1), so the hub does not decide which names exist.

**D5 — `/api/tools/*` and `/v1/audio/*` answer a timeout with a 504.** Before 89 no handler on those
routes caught the `TimeoutException`, so the caller got a bodyless 500. `DispatchDeadlineFilter` does
it once per route family, in that family's error shape (`deadline_exceeded` on OpenAI's), and only
before the response has started.

**D6 — a blocking job the hub gave up on is a failed request on `Metrics`.** Timed out, cancelled or
failed to send: the `finally` now calls `RecordRequestFail`. Before 89 only the registry was told, and
the in-flight gauge climbed by one for every such job; the stream paths always counted it.

**The node's own clocks are separate** (`Ollama:RequestTimeout`, `Upstream:TimeoutSeconds`, a
manifest's `requestTimeoutSeconds`). A hub deadline longer than the node's gains nothing.

Tests: `DispatchDeadlineTests` (Coordinator — lookup, binding, validator, a real `Dispatcher` whose
clock runs out), `ToolMeshTests`/`AudioEndpointTests`/`VideoJobTests` (Mesh — the 504s and a video
job failing on its own deadline; echo worker `--audio-delay-ms`).

### Phase 110 (a request waits at the hub for a free slot) — load-bearing

**D1 — `Queue:SlotWaitSeconds` holds a request while every holder of its model is full, and "full"
is one predicate.** `FleetSaturation.HasNoFreeSlot(registry, model, capability)`: somebody holds the
model, and every holder declared a slot count for it (109's per-model number, else 108's node
number) and runs that many of the hub's jobs for it. `FleetSaturation.HasFreeSlot` is what
`Router.FreeSlotTier` now reads too, so the router and the wait cannot disagree about "free". A node
that declared nothing is always free and nobody waits on its account. The wait runs **after** 25 D5's
cap queue, so `MaxConcurrency` stays that queue's business, and after the provider decision, so a
full slot never bursts (`IsSaturated` still reads caps only — a full slot means "this engine will
queue it", not "the fleet cannot answer"). Why it exists: two one-slot nodes and a third chat — the
router's tier was the identity, so the chat was committed to one engine's queue on arrival and
waited there while the other node went idle. **Considered and rejected: on by default** — a fleet of
two would start holding requests at the hub with no config changed. Default `0` is v3.73 exactly; the
status block's `slotWait` and the `inferhub_queue_slot_*` series are absent until it is set.

**D2 — A slot wait never refuses.** The engine queues; before this phase the request went straight
there and was answered. So the bound running out, `Queue:MaxDepth` being reached, and the caller's
own deadline all end with the request routed as v3.73 would have routed it — `sentAnyway` counts the
first two. **Considered and rejected: `MaxWaitSeconds` and its 503**, right for a declared cap (the
node *refuses* past it) and wrong for a slot (the engine *queues* past it). A caller who leaves is
an `OperationCanceledException`, as everywhere else.

**D3, load-bearing — one waiter per model at the head, and the next goes only once the previous is
counted.** A freed slot is seen by every poller at once; three pollers and one slot send three
requests to one engine, which is the thing the wait exists to stop. So each model (by
`NodeRegistry.ModelKey`) has a `SemaphoreSlim` turnstile; only its holder polls (every 50 ms), and it
keeps the turnstile, as a `SlotTicket`, until `InferenceCore` has **called** `DispatchWithFailoverAsync`.
That call returns its task only after `Dispatcher` has run `IncrementInFlight(connection, model)`,
which happens before its first await on both the blocking and the streaming path — so the moment the
ticket is disposed, the next head sees the slot taken. **If a future change puts an await in front of
that increment, this decision silently degrades to a stampede**; `OneFreedSlotLetsOneWaiterGoNotThree`
is the guard, and it is red with the turnstile removed. **Considered and rejected: per-node
reservations in the queue** — a second in-flight count the router would also have to read, to close
a gap the synchronous increment already closes.

**What it does not reach:** the warm tier (86 D3) still comes first, so a full warm node beats a free
cold one exactly as in v3.72; affinity still holds inside the free tier; tools and solo mode never
pass through here.

Tests: `SlotWaitTests` (Coordinator — three chats over two one-slot nodes through `InferenceCore`
with the real `Router`; the turnstile; the bound sending anyway; off by default; depth; cancel; 109's
number), `QueueTests`, `ModelSlotRoutingTests`.

### Phase 120 (the hub asks one node for its catalogue, estimate and install)

`NodeManagementControl` + `Endpoints/ManagementEndpoints.cs` (`/api/admin/nodes/{id}/fit/catalog|fit|fit/install`). A tool job
to that node alone (capability `manage`); the node's JSON is passed through, its refusal codes (`ManagementJob.StatusOf`)
become the status. Install = the node's agreed `ModelCommand` sent via `ModelCommandCoordinator.SendAsync(..., context)`.
A node without `Management` on its registration (before 3.81) is a 409 without a dispatch. Decisions: `src/InferHub.Node/Backends/CLAUDE.md` Phase 120.

### Phase 125 (the audit trail: who asked, which model, which node answered, when — never what was said)

`AuditTrail.cs` (`Audit:` options + validator, `AuditRecord`, `AuditScope`, the writer and the reader) and
`AuditMiddleware.cs` (the middleware, `GET /api/admin/audit`). The ask came from the "closed system" list of
*What I Build* (item 6): *who asked, which model, which node answered and when, without the content.*

**D1 — What a line holds, and what is kept.** `at`, `event`, `who` (the client id a key resolved to; `admin:` and
the first 8 hex of the admin key's SHA-256, set by `AdminApiKeyMiddleware`; `loopback`; `node` for the relay; `-`
for a key that resolved to nobody), `remote`, `id` (the trace id), `method`, **`path` — never the query string**,
`status`, `ms`, `model`, `capability`, `nodes`, `servedBy`. Never a body, a header value or a hash of any of them
(rule 7; the tests grep the files for a prompt, a query value and both keys). Audited surfaces: `/api`, `/v1`,
`/node/relay`, `/chatgpt-pairing`. **Reads are not actions:** `GET /api/status` and every admin `GET` are
skipped (the console polls them; they would bury the lines that matter) — except a refused one (401/403 is kept
on every method: a wrong key is the line an auditor wants first) and a read of the trail itself (who looked at who
did what is part of who did what). The middleware is **first** in the pipeline for the same reason: a request the
guards turn away never reaches anything after them. **Considered and rejected: the remote address as an option**
— a trail that cannot say where a stolen key was used from is not a security record.

**D2, load-bearing — the scope rides an `AsyncLocal`, written where the fact is known.** The middleware begins an
`AuditScope`; `InferenceCore.DispatchAsync` says the model and capability as asked (`AuditScope.Asked`, so a
request a provider answered or that was refused at admission still names its model); each of `Dispatcher`'s four
dispatch entries says the node (`AuditScope.Dispatched`), so **a failover appends** — `nodes` is every node a job of
the request was sent to, in order, and the last one answered it if `status` is 2xx. No endpoint passes anything
along: forty-odd endpoints reach the dispatcher, and one that forgot would be a silent gap. `Close()` is called
when the line is written; anything later (an image job's background dispatch that outlives its 202) is dropped,
not half-reported — **an async image or video job's node is therefore not on the submit's line** (named, not fixed).

**D3 — Off the request path, and a dropped line is counted.** One `BackgroundService` reads a bounded channel
(10 000) and appends to `audit-yyyy-MM-dd.jsonl` (UTC; flushed after each batch; a line that does not parse —
a torn tail after a crash — is skipped by the reader). `Write` never blocks and never throws: a full queue, or a
directory the writer cannot open, drops the line and counts it (`inferhub_audit_dropped_total`, a warning at most
once a minute). **`FullMode.Wait`, not `DropWrite`**: under `DropWrite` `TryWrite` answers true for a line it threw
away. **Considered and rejected: writing inline** — a slow disk would become every request's latency; **and
Postgres** — the files are the record an auditor can copy off the box without the hub running.

**D4 — Reading it.** `GET /api/admin/audit?from&to&who&node&model&event&status&limit` behind the admin key:
newest first, exact case-insensitive matches (`node` matches any node a failover tried), `limit` default 100, at
most 1000; reads day files newest-first and stops at the limit. With the trail off it answers 200 `enabled:false`
naming the key, so a console can ask without knowing.

**D5 — Rule 4's eighth exception.** Off by default (`Audit:Enabled`); when off the middleware passes straight
through without allocating a scope, nothing is created, `/api/status` has no `audit` key and `/metrics` no
`inferhub_audit_*`. Day files older than `RetentionDays` (default 90, 1–3660) are deleted at start and at each day
change — only files the trail named. Losing the directory loses history, never an answer. The image sets
`Audit__DataDirectory=/data/audit`. Each coordinator of a cluster (phase 32) writes its own trail.

**D6 — The fleet's door.** `NodeHub` writes `node.registered` (node id, name), `node.refused` (a wrong or missing
enrollment secret, `who` `-`) and `node.disconnected` (looked up before the registry forgets the connection), each
with the remote address and the connection id. The hub socket itself is not a request line: it lives for days.

Tests: `AuditTrailTests` (Coordinator — the real guards over a real socket, the file, the window, the filters, the
privacy grep, the scrape), `AuditTrailMeshTests` (real SignalR: a node registers, runs a job through the real
`Dispatcher`, a stranger is refused, the node leaves).

### Phase 126 (priority classes: when capacity runs short, essential work keeps running and background work waits)

`PriorityGate.cs` (`Priority:` options + validator, `PriorityClasses`, the gate) and three hooks: `InferenceCore.DispatchAsync`
(the background hold, the class into the capacity wait, essential past the slot wait), `RequestQueue.WaitForCapacityAsync(model,
priority, …)`, and `ClientContext.From` (the class, once per request). Chat and generate on both dialects only: embeddings
(`IEmbeddingDispatcher`), tools, audio, images and video do not pass through `InferenceCore` and are not classed.

**D1 — Three classes, set by the operator per client.** `Auth:Clients[].Priority` = `essential` | `normal` (default) |
`background`; a misspelt one fails startup (`ApiKeyOptionsValidator`) because the mistake would otherwise run silently as
normal. Anonymous and flat `Auth:ApiKeys` are normal. Off by default (`Priority:Enabled`): `ClassOf` answers `normal` for
everyone, `ClientContext.Priority` is null, no hook runs, `/api/status` has no `priority`, `/metrics` no `inferhub_priority_*`,
the audit line no `priority`. **Considered and rejected: numeric priorities** — a number invites a scheduler, and three names
are what an operator can reason about at 03:00.

**D2 — A request may lower its class, never raise it.** `X-InferHub-Priority` (same shape as 65's provider steer: it only
narrows). A batch job on an essential key can say `background`; a background key saying `essential` is ignored, as is an
unknown value. The class is on the audit line (125) while priority is on.

**D3, load-bearing — "capacity running short" is free places, and background never takes the last `ReserveSlots`.**
`FreePlaces(model, capability)` sums, over the nodes `FindNodesWithModel` routes to *now* (so a failed, paused, cordoned or
throttled node has already left the sum), the lesser of `MaxConcurrency − LocalInFlight` and the model's slots left (108/109).
Background work is held while that sum is `≤ ReserveSlots` (default 1) or while a higher class waits for the model; it polls
every 100 ms and goes when there is room, or answers 503 after `BackgroundMaxWaitSeconds` (default 600 — it is paused, not
refused). So when nodes fail or fill, background is what pauses first, with no "failure" signal of its own to get wrong.
**A node that declares neither a cap nor slots makes the sum unbounded (null) and nothing is held** — the hub cannot reserve
what it cannot count; every Ollama node declares slots (108), so in practice this is a node with neither and an unknown engine.
**Considered and rejected: stopping running background jobs** to make room — a half-generated answer is lost work, and a
preempted stream is a failure the caller sees; named as not done.

**D4 — A wait does not go ahead of a higher class.** `RequestQueue.WaitForCapacityAsync` (25 D5, the declared-cap queue)
registers each waiter with the gate by (model key, rank); a waiter is admitted only when the fleet is not saturated **and**
no higher class waits for the same model. Counts only — no second queue, the poll that existed decides.

**D5 — Essential skips the hub's slot wait.** 110's wait exists to be polite to an engine's FIFO; holding essential work at
the hub behind it would invert the point, so with priority on an essential request goes to the engine at once (it lands ahead
of everything the hub is still holding). Normal and background wait as in 110. The 110 D3 turnstile is untouched.

Tests: `PriorityTests` (Coordinator — the real `Router` and `RequestQueue` through `InferenceCore`: off by default; the reserve
held and released; a node removed makes background wait while essential and normal run; the 503; an unbounded node; the
header table; essential first in the capacity wait — red with D4's condition removed; essential past the slot wait; the
validators; the scrape).
