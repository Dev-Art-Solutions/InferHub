# InferHub v3.11.0 — configure the fleet, not the boxes

Twenty nodes has meant twenty `appsettings.json` files and twenty restarts. From v3.11 the
coordinator can say what a node should be doing — and the node decides whether it may.

```bash
curl -X PUT http://localhost:5080/api/admin/profiles/gpu-boxes \
  -H "X-Admin-Key: $ADMIN" -H 'Content-Type: application/json' -d '{
    "selector": { "labels": { "tier": "gpu" } },
    "capabilities": { "embed": false },
    "tools": { "whisper": true, "piper": false },
    "models": { "ensure": ["llama3.2"], "remove": [] },
    "maxConcurrency": 4
  }'
```

Every matching node applies what it can and reports back what it would not do and why.
`GET /api/admin/nodes/{id}/profile` answers the only question that matters afterwards: *I wrote that
and the box still does what it did before — why?*

## The hub can only ever narrow a node, and the check runs on the node

This is the decision the release exists around, and it is the one most systems in this space get the
other way round.

- A profile can switch a capability **off**. It cannot re-open one the box's own
  `Node:Capabilities:Disabled` closed.
- It can **stop** a tool. It cannot introduce one: a tool id that is not already in that node's
  `Tools:Allowed` is refused **by name**, and there is no field anywhere in a profile for a command,
  a path or an interpreter. That is why `Tools:Allowed` was a list and not a boolean in v3.9 — it was
  always going to be the ceiling this release could not raise.
- It can **lower** `MaxConcurrency`. Raising it is refused, because that number is a statement about
  hardware you own and the coordinator does not.

And the clamp that enforces all of it runs **on the node**, not on the hub. A clamp on the hub is a
clamp an attacker skips by not being the hub — the whole point is that a compromised or
misconfigured coordinator cannot turn a fleet of GPU boxes into fleet-wide remote code execution.
The adversarial suite drives the node's real application path with hostile profiles: a tool id that
is a path, an interpreter, a shell one-liner. Every one of them comes back as a refusal with a reason
naming the key that stopped it, and the running node is unchanged.

Profiles also add no authority over your data that the hub did not already have: `models.remove`
goes through the same v2.9 model-command channel as `DELETE /api/admin/nodes/{id}/models/{model}`.

## Desired state, so a rebooted node fixes itself

A profile is not a command. A node **asks** for its profile every time it registers, so a box that
was being rebuilt when you wrote one converges on the way back in — no operator action, and nothing
for the hub to remember about who has what. Re-applying the same revision changes nothing and says
so, which is exactly what makes that unconditional re-ask safe rather than a re-pull of forty
gigabytes of weights on every reconnect.

Refusals are **per item**: a profile asking for one impossible thing and four possible ones applies
the four and reports the one. A profile is never a startup dependency and **never restarts a node** —
switching a tool off stops its workers in place and withdraws its capabilities from the fleet;
switching it back on starts them again, with in-flight work undisturbed.

## Selectors, conflicts, persistence

Selectors are exact: a `nodeId`, or a set of `labels` of which **every** pair must match. No globs,
no expression language — a pattern dialect pointed at a security boundary is how somebody's node ends
up matched by a rule that reads correct. **A selector that names nothing matches nothing**, and is
refused with a 400 rather than quietly applying to every box you own.

Two profiles matching one node is a **conflict**, not a merge and not first-one-wins: neither is
sent, the node keeps what it last applied, and `/api/status` and the console say `conflict` until you
fix the selectors. Silent precedence is how a node ends up in a state no single document explains.

Profiles are in memory by default and a coordinator restart forgets them. `Fleet:Profiles:Persistence`
= `file` or `postgres` keeps them; `postgres` is what an HA pair wants, so both hubs read one fleet
configuration. Losing them is survivable by design — every node falls back to its own configuration,
which is never a wrong answer and never a capability nobody granted.

## Also in this release

- `/api/status` and `/api/admin/nodes` carry a per-node profile block: name, revision, status
  (`applied` / `pending` / `refused` / `conflict`), and the refusals with their reasons. The console
  and status page gain a **Profile** column.
- Every application is an audit event on the node it touched: `profile.apply:{name}@{rev}` by the
  admin caller, `profile.refused:{name}@{rev}` by the node.
- The clamped concurrency cap lands on the coordinator's registry entry directly, so lowering a cap
  takes effect on the next dispatch without the node reconnecting.

## Compatibility

Additive throughout. **A fleet that defines no profile behaves exactly as v3.10** — the profile
registry matches nothing, no new key appears in `/api/status`, and no node changes what it declares.
A v3.10 node against a v3.11 hub registers and serves normally; a v3.11 node against an older hub
gets no answer to its profile request, logs it at debug, and runs its own configuration.

`dotnet test`: **975 passed, 0 failed, 46 skipped** (was 946 at v3.10.1).

## Images

```
ghcr.io/dev-art-solutions/inferhub-coordinator:3.11.0
ghcr.io/dev-art-solutions/inferhub-node:3.11.0
ghcr.io/dev-art-solutions/inferhub-node:3.11.0-ollama
ghcr.io/dev-art-solutions/inferhub-node:3.11.0-tools
```
