# InferHub v3.91.0: one identity across the cluster, and automatic failback

Phase 131. Since 3.0 two coordinators can share one Postgres and take over from each other. Since 3.87 each node can carry
a certificate from its coordinator. The two did not fit together. The CA, the certificates it issued, pending tokens and
every node's organisation lived in files on the hub that issued them. With `Identity:Require` on, a failover locked every
certified node out of the coordinator meant to take over. A sealed node also kept downloading through the first hub in its
list, which was the one that had just failed. Now, in a cluster, the node identity record lives in the cluster's Postgres.
The relay follows the hub the node is connected to. Leadership can also return on its own to the hub that should lead.

```bash
# The same on every coordinator of the cluster:
Cluster__Enabled=true
Cluster__PreferredInstance=hub-a        # optional: the hub that leads whenever it is healthy
Cluster__FailbackAfterSeconds=60        # how long it must be back before the others hand the lease to it (>= the lease TTL)
```

## What it does

- **One record for the cluster.** With `Cluster:Enabled`, the node CA and its record live in the cluster's Postgres,
  in the table `node_identity` in `Cluster:Schema`. The hub that takes over verifies every certificate the old one issued,
  knows every organisation and sees every revocation. Writes are versioned: a write that lost to another hub's re-reads
  and re-applies, so neither change is lost. Two hubs booting at once on an empty database make one CA.
- **Upgrading keeps the nodes.** When the cluster has no CA yet, the first hub to boot moves its own CA and record from
  `Identity:DataDirectory` into Postgres, both in one transaction or neither. The nodes it certified stay certified.
  Another hub's files are left on its disk, not merged; its nodes enroll again.
- **A standby reads, and refuses writes.** A standby serves `GET /api/admin/identity` and the site checklist. It answers
  `503` to creating a token, revoking, a site plan's token, enrollment and renewal. Before it becomes active, a hub reads
  the record afresh.
- **Failback, opt-in.** Every member records that it is alive, by the database's clock. While the preferred hub is alive,
  no other hub takes the lease. When another hub has taken over, it hands the lease back only after the preferred hub has
  been alive for `FailbackAfterSeconds` without a break. A break restarts that count, so a flapping link moves
  leadership at most once. A failback shorter than the lease TTL fails at startup. Visible on `/api/status` as the
  cluster block's `detail`.
- **The relay follows the connected hub.** A sealed node's downloads go through the coordinator it is connected to, not the
  first in `Coordinator:Endpoints`. Only one of the node's own coordinators can become its relay.

## Upgrading

Without `Cluster:Enabled` nothing changes: the record stays in `Identity:DataDirectory`, as in 3.87. In a cluster the first
hub to boot on 3.91 moves its record in, so upgrade the hub that has been issuing certificates first. Without
`Cluster:PreferredInstance` whoever holds the lease keeps it, as before. The lease schema gains a table,
`{Table}_members`, created on boot. No new package.

## Found while building it

- **Bootstrap could deadlock.** The full suite against a real Postgres failed `ConcurrentBootstrapTests`: eight
  usage-ledger bootstraps at once deadlocked (`40P01`), because that bootstrap alters the table after creating it. The
  3.0.1 retry covered duplicate-creation errors only. Deadlocks are now retried too; the victim was rolled back whole, so
  that is safe. Five runs in a row pass.
- **An upgrade would have cost every certified node its certificate.** The first design started the cluster's record
  empty. A cluster already running 3.87 certificates would have made a new CA on upgrade. The one-time import fixes
  this, and its test fails without it.
- **Making the CA.** The first version copied the private key onto a certificate that already held it, and .NET refuses
  that. Caught by the first test run.

## Not established

- **Active-active.** One hub still serves at a time.
- **Merging two hubs' file records.** Only the first hub's record moves in.
- **A relayed model end to end after failover.** Live, the pull's first relay request went to the hub the node was on.
  The rest of the download was not waited for.

## Verification

- Suite, with a real Postgres: 2 817 passed, 35 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 1 060, Node 739,
  Mesh 549). New: `SharedIdentityTests` (what the active issued, the next active verifies; a revocation follows the lease;
  six hubs booting at once make one CA; ten tokens from two hubs at once are all kept; a hub joining with a file record
  brings its nodes, and the second hub does not, red without the import), `FailbackTests` (stand aside, a dead preferred,
  failback after 60 s, a flap restarts the clock, the validator, the standby's refusals),
  `PostgresClusterLeaseTests` (members by the database's clock), `SealedModeTests` (the relay follows the connected hub).
- Live, from source: two coordinators on one Postgres (`Identity:Require`, relay on, `PreferredInstance=hub-a`,
  `FailbackAfterSeconds=20`) and a sealed node with both hubs in `Coordinator:Endpoints`.
  - hub-b stood by: "the preferred coordinator 'hub-a' is alive". A token asked of hub-b was a `503`; hub-a made one.
  - The node enrolled through hub-a; hub-b listed its certificate at once.
  - hub-a stopped: hub-b became active (fence 2) and the node registered there with the certificate hub-a had issued.
  - A pull ordered through hub-b: the relay request went to hub-b. hub-a, first in the node's list, had none.
  - hub-a restarted at 03:03:11 and stood by. At 03:03:33 hub-b logged "failing back: the preferred coordinator 'hub-a'
    has been alive for 20s". At 03:03:37 the node was on hub-a.

## The published images

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.91.0` and `inferhub-node:3.91.0` both carry revision `553d687`. Both
ran as containers: two published coordinators on one Postgres, with the same settings as the live run, and a published
sealed node in a container with both hubs in its list.

- hub-b stood by for the preferred hub-a, and a token asked of it was a `503`. The node enrolled through hub-a, and
  hub-b listed its certificate.
- `docker stop` on hub-a at 03:40:11: by 03:40:26 hub-b was active and the node was on it, with the certificate hub-a
  had issued.
- `docker start` on hub-a at 03:41:07: at 03:41:31 hub-b logged "failing back … alive for 20s", and at 03:41:36 the node
  was on hub-a again.
- Not checked here: the relay pull. A sealed node in a container pulls through a registry on its own loopback, which the
  host's Ollama cannot reach, and the node refuses that by design. The relay pull was checked from source.
