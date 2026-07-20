# InferHub v2.12.0 — Stable-node affinity + optional persistence

Sticky routing keeps a conversation warm on the node that already loaded its model. Until now that
mapping was keyed to a SignalR **`connectionId`** and held only in memory, which had two costs: a
coordinator restart dropped every warm conversation, and — the non-obvious one — a `connectionId`
is **not stable across a node's own reconnect**, so a node bouncing its connection lost its warm
conversations even while it stayed up. This release re-keys affinity onto the stable **node id** and
then, opt-in, lets it survive a coordinator restart.

## What changed

- **Affinity keys on the stable `nodeId`, not the connection id.** `Record(conversationKey, nodeId)`;
  `GetNodeFor` returns a node id; the router resolves it to a live candidate at dispatch time. A
  sticky node that is disconnected, cordoned, or no longer holds the model is simply absent from the
  candidate set, so a stale hint is a clean miss and routing falls through to the best fresh node.
- **A node reconnecting keeps its warm conversations.** A disconnect no longer forgets affinity: the
  node comes back with a new connection id but the same node id, and its conversations resume on it.
  Node eviction doesn't forget either — an evicted node that re-registers picks its conversations
  back up, and the sliding window bounds the map for one that never returns. Only an **explicit
  admin deregister** — the operator saying a node is gone for good — forgets a node's affinity.
- **Optional file persistence, off by default.** `Affinity:Persistence` = `none` (default) | `file`.
  With `file`, the map is written to `Affinity:DataDirectory` as an append-log + periodic compacted
  snapshot — the same raw-store discipline the local vector store uses — and reloaded on startup with
  any entry past its sliding expiry dropped on load. Restart the coordinator and a still-fresh
  conversation stays pinned to its node with no cold reload.
- **Observability.** Live affinity entry count is on `/api/status` (`affinityEntries`) and `/metrics`
  (`inferhub_affinity_entries`, a fleet gauge present at zero).

## Invariants

- **Rule 4 holds.** Persistence is opt-in and off by default; with it off, behaviour is byte-identical
  to v2.11. When on, the file store is a **derived cache of routing hints**, never a source of truth:
  a lost or stale entry costs at most one cold model load, never a wrong answer, so it does not become
  a third authority alongside the vector store and the usage ledger. A torn last line from a crash
  mid-append is skipped on load, not treated as corruption.
- **Rule 7 holds.** The affinity key is still a conversation header value or a hash of the opening
  message — never content. The persisted record is `(conversationKey, nodeId, lastUsed)` and nothing
  more.
- **Rule 5 holds: zero new dependencies.** The file store is hand-rolled, exactly like the vector raw
  store.
- **Docker (D7).** The container points `Affinity__DataDirectory` at `/data/affinity`, under the
  `chown app:app /data` mount that `USER app` can write — the same trap the vector store hit, headed
  off in the same place. Inert unless persistence is turned on.

## Why this matters for v3.0

The upcoming warm-failover coordinator (phase 32) needs warm routing to survive a hub switch. That is
only possible once affinity keys on a stable identity and can be reloaded — which is exactly what this
release lays down. Phase 30 is the prerequisite, shipped on its own so it can be verified on its own.

543 tests green (17 Postgres-gated, skipped without a database). Verified live: a warm conversation
stays pinned to the same node across a coordinator restart with `Affinity:Persistence=file`, and
across a node reconnect without persistence.
