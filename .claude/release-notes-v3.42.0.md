# InferHub v3.42.0 — a node-owned collection survives its owning node's permanent loss

Phase 77. A node-owned (`local`-provider) collection has, until now, lived only on the disk of the
node that created it — if that box is lost for good, so is the corpus, silently. An admin can now
assign a **standby**: while the primary is healthy the hub relays its snapshot and every subsequent
write to the standby, and on confirmed permanent loss the standby is promoted to owner. The hub never
stores a copy of the vectors itself — `CollectionOwnership`'s own "this hub deliberately holds no copy
of it" stays literally true throughout.

## What's new

- **`POST /api/admin/collections/{collection}/standby/{standbyNodeId}`** and
  **`DELETE /api/admin/collections/{collection}/standby`** — assign or clear a standby for a
  node-owned collection. Assigning one immediately asks the primary for a full snapshot.
- **Live relay, not periodic resync.** The primary's writes are tailed and forwarded as they happen
  (opt-in on the node via `LocalApi:Retrieval:ReplicateOwnedCollections`), reusing phase 15/16's own
  wire shapes (`VectorReplicaAssignment`/`VectorReplicaOp`) and the node's existing `ReplicaStore`
  verbatim — the standby's receiving side needed **zero new storage code**.
- **`CorpusFailoverService`** — a grace-period watcher (`CorpusFailover:Enabled`, default off;
  `GraceMinutes`, default 10) that promotes a standby once its primary has been absent from the fleet
  past the grace period — a confirmed, sustained absence, never a bare disconnect or a routine
  redeploy.
- **Promotion reuses the ordinary profile-assignment path.** The standby moves its held replica into
  its own corpus directory, and the hub writes the standby's profile to include the collection through
  the exact mechanism an admin's own `.../collections/{c}/assign` already uses. There is no second,
  undocumented way for a node to end up owning a name.
- **No automatic failback.** A primary that reconnects after its standby was promoted holds a stray
  copy with no owner claim; reconciling it is a manual admin action.

## What was not established

- **Scope is `local` provider only.** `qdrant`'s store fires no change events for the tail hook to
  read (the same "never widen replication past `LocalVectorStore`" boundary phase 44 already drew),
  and `postgres` has no credential-ref path (phase 71 D5). A standby-assignment attempt for either is
  silently never tailed rather than refused by name — a known gap, not a designed refusal.
- **`CorpusFailoverService`'s own interval/grace-period timer was not run end to end.** Only
  `NodeCorpusReplicator.PromoteAsync` (what the timer calls once grace elapses) was exercised, driven
  directly over a real two-node mesh.
- **No published-image verification for this release.** Everything below ran as in-process .NET test
  objects — a real `NodeHub`, two real `CoordinatorConnection`s, two real `local`-provider
  `RetrievalHost`s on separate disks — not containers. No Docker image was pulled and driven with a
  real standby assignment and a real kill of the primary container.
- **No console panel.** A standby's existence is visible only through `CollectionOwnership`'s own
  state and the audit log (`corpus.standby.assign`/`corpus.promoted` entries), not through the
  management UI.
- **`appsettings.json`'s new `CorpusFailover` and `ReplicateOwnedCollections` keys are documented**,
  but the `Coordinator/Vector/CLAUDE.md` decision block and this release together are the only places
  the design is written down — no config-reference table entry was added beyond the inline comments.

## Verification

`dotnet test InferHub.sln` — full solution, not just this phase's slice: **1531+ tests, 0 failures**,
unchanged from pre-phase (the new opt-in config defaults to off on both ends, so nothing pre-existing
moved). Two new focused suites:

- `tests/InferHub.Tests.Coordinator/Vector/CollectionOwnershipStandbyTests.cs` (5/5) — the standby
  bookkeeping in isolation: a hub-owned collection refuses a standby, a node cannot be its own
  standby, and a standby assignment survives an unrelated `Rebuild` (the exact hazard this phase's own
  research found in the ownership map it deliberately did not reuse).
- `tests/InferHub.Tests.Mesh/NodeCorpusReplicationTests.cs` (2/2) — a **real** two-node SignalR mesh,
  not mocked: a real `NodeHub`, two real `CoordinatorConnection`s, two real `local`-provider
  `RetrievalHost`s on separate disks. The first case proves the initial snapshot and a live write both
  land on the standby's `ReplicaStore`. The second drives the full sequence — assign standby, write,
  disconnect the primary, call `PromoteAsync` directly (what the grace-period timer would call), and
  **query the promoted standby's own corpus**, getting the pre-kill record back — not just a check
  that ownership metadata moved.

**Not run for this release: the published-image pull-and-drive step.** This is named rather than
skipped quietly, per this project's own rule about what "done" means for a phase that changes a
running process's behaviour under failure. A real two-node Docker deployment, a real standby
assignment, and an actual `docker kill` on the primary is the first thing to run against the published
`3.42.0` images.
