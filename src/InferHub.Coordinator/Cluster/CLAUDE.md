# InferHub.Coordinator/Cluster — agent context

**Scope: `src/InferHub.Coordinator/Cluster/`.** The multi-coordinator lease, the split-brain fence,
the standby's refusal set and the signals a load balancer in front of two hubs needs.

> **Read the root `CLAUDE.md` first**, then `src/InferHub.Coordinator/CLAUDE.md` — this is a subtree
> of that host and every rule there still binds.

**Split out in phase 69 (69 D8), for 62 D6's and 67 D6's reason and no other:** the coordinator's
file was at 1076 of 1100 and a phase cannot land its decisions in a file with one line of headroom.
Phase 32 is the largest coherent subtree the backend-health phase had nothing to do with, and it
moved **whole and unedited**.

## Related context

- The host this belongs to: `src/InferHub.Coordinator/CLAUDE.md`
- The node that reconnects when leadership moves: `src/InferHub.Node/CLAUDE.md`

## Decisions recorded here

### Phase 32 (multi-coordinator: standby hub & warm failover) — also load-bearing

**D1 — Standby and active share the *same* Postgres, so rule 4 is untouched.** There is no new
source of truth: the lease row is a mutual-exclusion token, never state anyone reads to answer a
request, and the vector store and usage ledger are the same external stores both hubs already
used. The coordinators are interchangeable readers/writers of one durable store, not two
authorities. Everything else on a hub (registry, affinity, metrics, audit) is *derived* and
rebuilds as nodes reconnect — which is exactly why a promoted standby needs no migration step.
**HA targets `postgres` only.** Under `local` the raw store is per-hub; clustering it is future
work, and `Cluster:Enabled=true` over a `local` store would be two authorities wearing one name.

**D2 — A lease row, not a PG advisory lock.** The obvious alternative was rejected on purpose: an
advisory lock is scoped to a *session*, so a pooled connection dropping silently releases
leadership with nothing to observe, and it carries no expiry and no fence a partitioned holder can
reason about **locally**. [PostgresClusterLease](src/InferHub.Coordinator/Cluster/PostgresClusterLease.cs)
is one conditional upsert — `ON CONFLICT DO UPDATE … WHERE holder = me OR expires_at <= now()`,
`RETURNING` — decided entirely by the database clock, so there is no read-then-write window two
coordinators can both walk through. The fence counter bumps only on a change of holder, never on a
renewal: a bumped fence is how an operator knows leadership actually moved.

**D3 — The split-brain guard is local, and the trade is deliberate.** A partitioned active hub
cannot *be told* it lost the lease — by definition it cannot reach the database that knows. So
[ClusterLeaseService](src/InferHub.Coordinator/Cluster/ClusterLeaseService.cs) demotes when this
instance has not **proved** leadership within the TTL, measured on its own clock from the last
successful renewal. That is the same deadline Postgres uses to hand the lease over, so the two
windows cannot overlap with both hubs serving. The consequence — an unreachable database demotes a
healthy primary after one TTL, taking the mesh down — is correct and is not to be softened: a
request the mesh cannot attribute to a single leader is worse than a `503` a load balancer routes
elsewhere. `Cluster:RenewIntervalSeconds` is validated at ≤ TTL/3 so ordinary packet loss cannot
flap leadership. A clustered hub starts **standby** and is promoted only on a real acquisition;
starting active would give every cold boot a two-primary window.

> **The deadline is checked *before* any I/O, and the attempt is bounded by what is left of it.**
> Found by pulling the plug on Postgres under the running stack: the round-trip itself burned
> Npgsql's connect timeout, so demotion landed at **23s on a 15s TTL** — and the row frees at 15s.
> That 8s gap is a window in which the standby holds the lease and the old primary still believes
> it leads: precisely the split brain the fence exists to prevent. The loop's sleep is clamped to
> the remaining time too, so tick granularity cannot add slack either. A fence that can be
> outrun by its own health check is not a fence, and only running it found that —
> `SplitBrainTests.TheFenceDoesNotWaitForTheRoundTripToComplete` pins it.

**D4 — Node failover is enforced in the middleware, not in the hub, because a `HubException` from
`OnConnectedAsync` does not fail the client's `StartAsync`.** Found live: by the time
`OnConnectedAsync` runs the handshake has completed, so throwing (or `Context.Abort()`-ing) leaves
the node believing it connected, only to be dropped a beat later with no reason attached — it
cannot tell "standby, try the next endpoint" from "hub is broken". So `/hubs/node` is in
[ClusterRoleMiddleware](src/InferHub.Coordinator/Cluster/ClusterRoleMiddleware.cs)'s refusal set and
a standby answers the *negotiate* with the same `503` clients get. `NodeHub` keeps its own check as
defence in depth. **Do not "simplify" the middleware entry away** — the hub check alone does not
work, and `FailoverTests` crosses the real wire precisely so that cannot regress unnoticed.

**D5 — The hub does not become a load balancer; it becomes honest.** Client failover is a TCP/HTTP
LB or DNS in front of both hubs. What InferHub owes that front is signals: `X-InferHub-Role` on
every response, `role` on `/health`, and a `503` + `Retry-After` on inference against a standby, in
the caller's own dialect (OpenAI envelope on `/v1`, per phase 21/29). **`/health` stays `200` on a
standby** — a standby *is* healthy, it just is not leading, and reporting otherwise has an
orchestrator restart-loop the instance that is supposed to be waiting quietly. Drain on the role or
the inference `503`. Unlike phase-25 admission (which lives in `InferenceCore` because it needs the
model name), the role decision needs nothing from the body, so it belongs in the pipeline before
routing, deserialization or a queue wait.

**D6 — What a standby refuses is a short, explicit list, and status is not on it.** Inference,
ingestion, search, the vector data plane and the node hub. `/health`, `/api/status`, `/metrics`,
`/api/admin/*` and the status page stay served, because "why is nothing being served?" has to be
answerable *from* the instance that stopped serving. A standby that goes dark is a standby nobody
can diagnose.

**D7 — `IF NOT EXISTS` is not atomic, and this is the first phase where that is reachable.** The
existence check and the catalog insert are separate steps, so two coordinators booting at the same
instant both pass the check and one dies on a unique index in `pg_extension` / `pg_namespace` /
`pg_class`. Everywhere else in InferHub bootstrap happens once on one hub, so the race never fired;
here simultaneous startup is the *normal* case, and an HA pair that crashes half of itself on a cold
boot is not HA. [ConcurrentDdl](src/InferHub.Coordinator/Postgres/ConcurrentDdl.cs) is the one place
that retries it — the other session winning **is** success — and **all three** Postgres bootstraps
(the lease, the vector store, the usage ledger) go through it.

> **This shipped broken in v3.0.0, in the two paths that were noted-but-not-fixed.** The lease was
> hardened during the phase; the note said "if the vector store or the usage ledger ever bootstrap
> concurrently, they need the same treatment" — and then v3.0.0 tagged without doing it. Pulling the
> published images and cold-booting two hubs against an empty database, `hub-a` exited 139 on
> `pg_extension_name_index` while `hub-b` came up fine, and the error text blamed a missing
> privilege, sending the operator after a DBA for a problem that was a race. Fixed in v3.0.1.
> `ConcurrentBootstrapTests` races eight of each against a real Postgres and fails without the
> retry. **A hazard you have written down but not fixed is still shipped** — and D7 exists because
> that class of thing is only ever found by running the artefact.

**Rule 5 survived again.** Phase 32 added **zero** new dependencies: the lease is `Npgsql`, already
recorded for the `postgres` vector provider, and the standby refusal is `System.Text.Json`.

