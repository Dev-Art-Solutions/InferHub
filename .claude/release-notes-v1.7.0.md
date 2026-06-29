Replicas, fleet-wide. v1.7.0 spreads each collection's index across the connected
nodes and lets queries be served by a node, not just the coordinator. The hub's raw
store stays the source of truth; node copies are derived. Writes land in the raw
store first, then fan out to the holders.

## What's new

- **Replica placement.** The coordinator targets `VectorStore:ReplicationFactor`
  (default `2`, capped at the eligible node count) replicas per collection. Cordoned
  nodes are skipped; spread before doubling up. Placement is recomputed whenever the
  fleet changes (register, deregister, cordon, disconnect).
- **Server→node push protocol over the existing SignalR link.** New methods —
  `AssignVectorReplica`, `ApplyVectorOp`, `DropVectorReplica` — let the hub seed a
  fresh replica with a snapshot, forward subsequent upserts/deletes, and release a
  replica when placement changes. Nodes expose nothing inbound.
- **Node-served queries.** A new `vector-query` job kind threads through the
  dispatcher; nodes execute searches against the local replica and return matches.
  Reads prefer a node holding a replica (round-robin among the least-busy),
  falling back to the coordinator's hub-local index when none is available.
- **Hub-local index is the floor.** Single-node, zero-node, and many-node deployments
  all answer queries. The raw store on the always-on hub anchors the durability story.
- **Node-side persistence.** Replicas are kept under `Vector:ReplicaDirectory`
  (default `./data/vector-replicas`) with the same append-only-log + snapshot shape
  as the hub. The node rebuilds the index from disk at startup.
- **No full re-push on restart.** A node reports its on-disk replica inventory
  `(collection, lastSeq)` in `Register`. The coordinator recognises matches and skips
  the re-push; stale or unknown entries fall through to the placement loop, which
  heals them.
- **Admin visibility.** `GET /api/admin/vector/collections` now returns a `placement`
  block per collection — target vs live replicas and the node IDs that hold them.
- **Storage primitives moved to `InferHub.Shared`.** `RawCollection`, `FlatIndex`,
  and `DistanceMetric` now live under `InferHub.Shared.Vector.Storage` so both hub
  and node can use them. No behaviour change for the existing hub-local store.

## Compatibility

- The raw-vector and text-vector paths from v1.5/v1.6 are unregressed. With no nodes
  connected (or with `ReplicationFactor=0`), reads and writes still hit the hub-local
  store exactly as before.
- `VectorStore:Enabled=false` (the default) keeps the original no-persistence
  contract: no replica protocol, no endpoints, no on-disk state.
- `NodeRegistration` gains an optional `Replicas` field. Older nodes that omit it
  keep working — they're treated as having no on-disk inventory.

## Config

- `VectorStore:ReplicationFactor` (default `2`) — target replicas per collection,
  capped at the eligible node count.
- `Vector:ReplicaDirectory` (node, default `./data/vector-replicas`) — where the node
  persists assigned replicas.

## What's next

Phase 16 (`v1.8.0`) makes the mesh self-healing: when a node holding a replica drops,
the coordinator rebuilds it on a survivor from its raw store, automatically, back to
the target replication factor.
