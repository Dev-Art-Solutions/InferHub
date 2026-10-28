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
