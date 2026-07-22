# InferHub v3.0.1 — two coordinators can boot at the same time

**Upgrade if you run v3.0.0 with `Cluster:Enabled=true`.** A two-coordinator cold start against a
database that has not been bootstrapped yet could kill one of the hubs at startup.

## The bug

`CREATE EXTENSION / SCHEMA / TABLE / INDEX ... IF NOT EXISTS` is **not atomic** in PostgreSQL: the
existence check and the catalog insert are separate steps. Two sessions racing the same statement
can both pass the check, and the loser dies on a unique index in `pg_extension`, `pg_namespace` or
`pg_class`.

Before v3.0 this was unreachable — bootstrap happened once, on the one coordinator. HA makes
simultaneous startup the *normal* case. On a cold start against an empty database, one hub exited
with

```
Failed to CREATE EXTENSION vector. The DB role lacks the privilege — have a DBA run
'CREATE EXTENSION vector' once ... Underlying error: duplicate key value violates unique
constraint "pg_extension_name_index"
```

while the other came up fine. The message is also a misdiagnosis: it blames a missing privilege and
sends the operator after a DBA, for a problem that is a race and clears itself on a retry.

**Symptoms:** one coordinator exits at startup (`139`) on the *first* boot against a fresh database,
usually the one that lost the race, on `pg_extension_name_index` or `pg_namespace_nspname_index`.
The surviving hub takes the lease and serves normally, so the mesh looks healthy while the pair is
silently down to one — no standby, no failover. A restart of the crashed hub succeeds, because by
then the objects exist.

**Not affected:** `Cluster:Enabled=false` (single coordinator — bootstrap is not raced), and any
deployment whose Postgres schema and extension already existed before the upgrade.

## The fix

One place decides: [`ConcurrentDdl`](../src/InferHub.Coordinator/Postgres/ConcurrentDdl.cs) retries
a bootstrap statement when it fails with a concurrent-creation SQL state (`23505`, `42P07`, `42710`,
`42P06`) — the other session winning **is** success, and by the retry the object exists and the
`IF NOT EXISTS` is a no-op. Only those states, only a bounded number of attempts: a genuine
privilege error or a bad identifier still fails fast and loudly, which is why the bootstrap is
allowed to kill the process at all.

All three Postgres bootstraps now go through it — the **coordinator lease**, the **vector store**
(extension, schema, registry table, keyword indexes) and the **usage ledger**.

## Why v3.0.0 shipped with it

The lease bootstrap *was* hardened during the phase, after eight racing contenders turned it up in
testing. The design note recorded the obvious next thought — "if the vector store or the usage
ledger ever bootstrap concurrently, they need the same treatment" — and then v3.0.0 was tagged
without doing it. A hazard you have written down but have not fixed is still shipped.

It was caught by the release's own D7 step: pull the published images, run the feature for real. The
unit suite was green throughout, on both sides of the bug.

## Verified

- `ConcurrentBootstrapTests` races eight coordinators through each of the three bootstraps against a
  real Postgres. It **fails without the retry**, reproducing both catalog violations and the
  misleading privilege message — the reproduction was confirmed before the fix was kept.
- 640 tests green with the Postgres gate open (0 skipped).
- Two hubs cold-booted on the published images against an empty database: both reach `ok`, one
  active and one standby.
