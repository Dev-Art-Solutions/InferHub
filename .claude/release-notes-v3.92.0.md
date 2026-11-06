# InferHub v3.92.0: active-active coordinators, first step

Phase 132. Since 3.0, two coordinators can share one Postgres, but only one serves at a time and the other waits. Now
they can all serve at once. Each node connects to one member. A request that reaches a member whose own nodes can't
serve its model goes to a member whose nodes can, and the answer streams back. Put a load balancer in front of all
members; any of them answers any request.

```jsonc
"Cluster": {
  "Enabled": true,
  "Mode": "active-active",               // "standby" (the default) is unchanged
  "InstanceId": "hub-a",
  "AdvertiseUrl": "http://hub-a:8080/"   // where the other members reach this one
}
```

This is the first of several phases. What still differs per member is listed below.

## What it does

- **Every member serves.** Nobody contends for the lease. Each member writes a heartbeat every few seconds with its URL
  and the models its nodes can serve. If a member can't write that heartbeat within `LeaseTtlSeconds`, it stops
  serving and drops its nodes, like a fenced active in standby mode. Nodes connect to any member and need no change.
- **A request finds its node on any member.** A request is passed on only when no node of this member can serve it and
  another member's nodes can. "Can serve" is the router's own test under the caller's organisation: no unhealthy,
  over-limit, paused or cordoned holder counts. The request is passed on once and never again. All headers go with
  it, so the member that serves it authenticates, limits, meters, audits and scopes it as if the client had called it
  directly; that is why all members need the same `Auth` section. The answer streams back as it is produced, with
  `X-InferHub-Forwarded-To`. If the other member doesn't answer, the member the client reached serves the request
  itself. The order is: this member's nodes, then another member's nodes, then cloud providers.
- **The listings show the cluster.** `/v1/models` and `/api/tags` include what the other members serve, after this
  member's own models and within the caller's organisation.
- **Job ids.** `GET`/`DELETE /api/images/jobs/{id}` for an id this member did not issue goes to the other members in
  turn, so a client behind a balancer can poll any of them.
- **Certificates.** Every member can create tokens, enroll nodes and revoke them in the shared record. A copy of the
  record is trusted for at most two seconds, and every change re-reads it first. A node revoked on one member is
  dropped by the member it is connected to within one renew interval.
- **Visible.** The cluster block on `/api/status` gains `mode` and `peers`: each other member's URL, last heartbeat,
  model count and how many requests were passed to it (served, unreachable, missing). `/metrics` gains
  `inferhub_cluster_peers` and `inferhub_cluster_forwarded_total{peer,outcome}`.

## Upgrading

Without `Cluster:Mode=active-active` nothing changes: standby mode, its status block and its metrics are as in 3.91.
The members table gains two columns, `url` and `models`, added on boot. With active-active, `AdvertiseUrl` is required.
`PreferredInstance` and `CorpusFailover:Enabled` are refused at startup: there is no leadership to hand back, and corpus
failover would promote a standby whose owner is connected to another member. No new package.

## Still per member

- **Client limits and queues.** A client behind a balancer can reach each member's limits.
- **Job lists.** `/api/images/jobs` and `/api/videos/jobs` list this member's jobs only.
- **The relay's cache and supply, and priority.**
- **Models a node serves as a tool** (voices, image models) are not passed on.
- **Busy, not missing.** A request is passed on when no node here can serve the model at all, not when this member's
  nodes are merely busy.

## Found while building it

- **A revocation could be lost.** In the first version, a member decided a change from its two-second copy of the
  record. A revocation on hub-b, made from a copy older than hub-a's enrollment of that node, found no certificate and
  wrote nothing. The version check can't catch that, because nothing is written. Every change now starts from a fresh
  read. Found by `SharedIdentityTests`; standby mode was not affected, because only one member writes there.
- **A forwarded request lost its body when the other member didn't answer.** The first version wrapped the request
  body in the outgoing request, and sending it disposed the body. The member then had nothing to serve. It now sends a
  copy. The test fails without the fix.

## Verification

- Suite, with a real Postgres: 2 837 passed, 35 skipped, 0 failed (Desktop 36, Shared 433, Coordinator 1 080, Node 739,
  Mesh 549). New tests:
  - `ClusterForwardingTests`, over real sockets: forwarding with the first chunk arriving before the other member
    finishes, local models, unknown models, no second hop, a dead member, a job id found past a member that answers
    404. Mutation-checked: removing the loop guard, buffering the answer, and losing the body each turn a test red.
  - `ActiveActiveTests`: the heartbeat as proof and the fence, the peer directory, organisation scope, listings, the
    advertisement, the validator, status and metrics, and the revocation sweep.
  - `SharedIdentityTests`: tokens and enrollment across members, a revocation seen within two seconds.
  - `PostgresClusterLeaseTests`: the members table round trip.
- Live, from source: two members on one Postgres with certificates required. node-1 (only `qwen2.5:0.5b`) was on
  hub-a and node-2 (only `smollm:135m`) on hub-b. Each node enrolled with a token made on the other hub.
  - Both hubs listed both models.
  - hub-a answered `smollm:135m` through hub-b, and hub-b answered `qwen2.5:0.5b` through hub-a
    (`X-InferHub-Forwarded-To`). hub-a's own model was answered locally.
  - A streamed `/api/chat` through hub-a arrived token by token, about 125 ms apart.
  - hub-b's audit line named the model and node-2.
  - node-1 was revoked through hub-b at 23:16:12.24. hub-a dropped it at 23:16:12.98, and every reconnect was refused.
  - hub-b was stopped. A request on hub-a for its model was tried there once (counted `unreachable`) and answered by
    hub-a (404, no holder). After the TTL hub-b left hub-a's peers and listing.
