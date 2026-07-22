# InferHub v3.0.0 — Multi-coordinator: standby hub & warm failover

The always-on hub is the mesh's single durability anchor by design. That also made it the single
point of failure: nodes could come and go, the coordinator could not. v3.0 removes that — run a
second coordinator as a **warm standby** over the same Postgres, and the mesh survives losing one.

This is the **foundation of the HA track, not the whole thing**, and the version number is for the
former. Active-active load sharing and clustering the `local` vector provider are still future work,
stated plainly here and in the README rather than implied by a major version.

## What changed

- **`Cluster:Enabled=true` makes coordinators contend for a lease.** Exactly one is **active** and
  serves inference; the rest run **standby** — they answer `/health`, `/api/status`, `/metrics` and
  `/api/admin/*`, and return `503` + `Retry-After` on every inference route, in the caller's own
  dialect (the OpenAI error envelope on `/v1`).
- **Every response from a clustered hub carries `X-InferHub-Role: active|standby`**, and `/health`
  gains `role` and `instance`.
- **Nodes take a list.** `Coordinator:Endpoints` replaces the single `Coordinator:Url` for HA
  deployments (empty = `Url`, exactly as before). A standby refuses the SignalR negotiate with a
  `503`, so walking the list is how a node finds the current leader. On demotion the old primary
  drops its node connections so they go looking immediately rather than waiting on a heartbeat.
- **Failover needs no manual step and no migration.** The durable state — the vector store and the
  usage ledger — already lived in Postgres; the promoted hub reads the same rows. Everything else on
  a coordinator (registry, affinity, metrics, audit) is derived and rebuilds as nodes reconnect.
- **A clean shutdown releases the lease**, so a planned failover is instant rather than one TTL.
- **New metrics:** `inferhub_cluster_active{instance}` and `inferhub_cluster_fence{instance}`.
  Alert on `sum(inferhub_cluster_active) != 1` — `0` means no leader, `2` would mean the fence
  failed. `/api/status` gains a `cluster` block. Both are absent entirely without `Cluster:Enabled`.
- **A two-coordinator compose overlay** (`deploy/docker/compose.ha.yml`) with an nginx front and a
  failover runbook.

**This requires `VectorStore:Provider=postgres`** — that is where the durable truth already lives
outside any one coordinator. Under `local` the raw store is per-hub, so a `local` deployment stays
single-coordinator.

## Invariants

- **Rule 4 holds: no new source of truth.** Standby and active share the *same* Postgres. The lease
  row is a mutual-exclusion token, never state anyone reads to answer a request. The coordinators
  are interchangeable readers/writers of one durable store, not two authorities.
- **Rule 5 holds: zero new dependencies.** The lease is `Npgsql`, already recorded for the
  `postgres` vector provider. No ZooKeeper, no etcd, no gossip layer.
- **A lease row, not a PG advisory lock.** An advisory lock is scoped to a *session*, so a pooled
  connection dropping silently releases leadership with nothing to observe — and it carries no
  expiry and no fence a partitioned holder can reason about locally.
- **The split-brain guard is local, and the trade is deliberate.** A partitioned primary cannot be
  *told* it lost the lease; it cannot reach the database that knows. So it demotes when it has not
  **proved** leadership within the TTL, on its own clock. An unreachable database therefore takes
  the mesh down rather than letting two hubs both serve. A request the mesh cannot attribute to a
  single leader is worse than a `503` a load balancer routes elsewhere.
- **The hub does not become a load balancer.** Client failover is an LB or DNS in front. What
  InferHub owes it is honest signals, and `/health` is deliberately still `200` on a standby: a
  standby *is* healthy, it just is not leading. Drain on the role, not on `/health`, or an
  orchestrator will restart-loop the instance that is supposed to be waiting quietly.
- **`Cluster:Enabled=false` is byte-identical to v2.13** — no lease, no connection, no role header,
  no `cluster` key on `/api/status`, no cluster series on `/metrics`.

## Two bugs this release found by being run, not by being tested

Both were caught in a live failover rehearsal against the built images, and neither could have
surfaced from the unit suite.

- **The fence could be outrun by its own health check.** With Postgres pulled out from under a
  running stack, the renew attempt burned Npgsql's connect timeout, so demotion landed at **23s on
  a 15s TTL** — while the row frees at 15s. That 8s gap is a window in which the standby holds the
  lease and the old primary still believes it leads: exactly the split brain the fence exists to
  prevent. The deadline is now checked **before** any I/O, the attempt is bounded by what is left of
  it, and the loop's sleep is clamped so tick granularity adds no slack. Fenced at 15s, measured.
- **`CREATE SCHEMA IF NOT EXISTS` is not atomic.** Two coordinators booting at the same instant both
  pass the existence check and one dies on `pg_namespace`'s unique index. Everywhere else in
  InferHub bootstrap happens once on one hub, so the race was never reachable; here simultaneous
  startup is the *normal* case, and an HA pair that crashes half of itself on a cold boot is not HA.
  Found by racing eight contenders against a real Postgres.

## Upgrading

Nothing to do — HA is opt-in and off by default. To turn it on, give both coordinators the same
Postgres and differing instance ids:

```jsonc
"Cluster": {
  "Enabled": true,
  "InstanceId": "hub-a",       // "hub-b" on the other
  "ConnectionString": "",      // env Cluster__ConnectionString — the SAME database
  "LeaseTtlSeconds": 15,       // worst-case failover delay AND the fencing window
  "RenewIntervalSeconds": 5    // must be <= a third of the TTL; startup fails otherwise
}
```

and point the nodes at both:

```jsonc
"Coordinator": { "Endpoints": [ "http://hub-a:8080/", "http://hub-b:8080/" ] }
```

Then put a load balancer in front, draining on `X-InferHub-Role` or on the inference `503`. The
compose overlay and runbook in [`deploy/docker/`](../deploy/docker/README.md) do all of this.

## Verified

- 637 tests green, including the Postgres-gated suites run with the gate **open** (0 skipped):
  `ClusterLeadershipTests`, `FailoverTests` (real Kestrel + real SignalR), `SplitBrainTests`,
  `PostgresClusterLeaseTests`.
- A live failover rehearsal on the built images: leadership elected, node rotated past the standby's
  `503` onto the active hub, active hub stopped → standby promoted immediately (clean release) →
  node re-registered in ~8s → the nginx front served from the new leader. Then Postgres killed under
  the active hub → fenced to standby at the TTL, node connections dropped, inference `503`.
