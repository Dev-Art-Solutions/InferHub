# InferHub v3.98.0: community groups — machines of different owners, by choice

Phase 138 of the sharded-models track. Until now a split model ran in a private cloud: one organisation, members the admin
named. This release adds the second mode: people who share compute by choice, machines of different owners and possibly
different organisations, put together by the coordinator.

Its price is said up front and not hidden: **every machine in a community group sees the prompts and answers that pass
through it.** Nothing encrypts what the workers compute on, by decision for now. So three people each have to say yes,
separately.

```jsonc
// each machine's owner — both keys, or the node stays private
"Node": { "Shards": { "Enabled": true, "RamGiB": 3, "Community": true, "CommunityAcceptRisk": true } }
// the admin — the coordinator picks the workers
"ShardGroups": { "llama-8b-community": { "Mode": "community", "Head": "shard-a", "Workers": 2, "Model": "llama31-8b.gguf" } }
```

```http
POST /v1/chat/completions   X-InferHub-Community: accept   {"model": "llama-8b-community", ...}
```

Nothing changes for a deployment that sets none of this.

## What it does

- **The machine's owner opts in, with the risk accepted in its own configuration.** `Node:Shards:Community` alone is
  ignored, and the node's shard state says why; it needs `CommunityAcceptRisk` too.
- **A sealed node never joins.** A node with `Node:Sealed` and `Community` refuses to start, naming it with the other doors
  a sealed node keeps shut. The coordinator also never counts a node that registered as sealed.
- **The coordinator picks the members.** A group with `Mode: community` names its head and how many workers it wants. The
  head must have joined the community too, since it sees every prompt. The workers are the ready community nodes with the
  largest offers. If too few can take part, the group says how many it has and how many it wants. A member that leaves is
  replaced by the next pick.
- **Community nodes reach each other across organisations.** Every other node still reaches only its own organisation.
- **The client accepts in each request.** Without `X-InferHub-Community: accept`, a request for a community group gets
  `403`: *model '…' is a community group: it runs on machines of other owners, and they can see this prompt and the answer as
  they pass through. To send it anyway, add the header X-InferHub-Community: accept*. Both API dialects check it.
- **Visible where clients look.** `/v1/models` lists the group with `owned_by: inferhub-community`. The console marks
  community nodes and community groups.

## Measured in the lab

`deploy/lab/shards/community-lab.sh`: shard-a (the head), shard-b and shard-c opted in; shard-d offered the same memory and
did not; shard-s was sealed and asked to join anyway.

- The coordinator picked **shard-b and shard-c**, never shard-d.
- shard-s **exited at startup**, naming `Node:Shards:Community`.
- `/v1/models`: `{"id":"llama-8b-community","owned_by":"inferhub-community",…}`.
- A chat without the header got the `403` above, on `/v1/chat/completions` and on `/api/chat`. With the header, the group
  answered.

## The published images

`inferhub-coordinator:3.98.0` and `inferhub-node:3.98.0`, revision `7ff5314`, through `community-lab.sh`: the coordinator
picked shard-b and shard-c, the sealed node exited naming the door, `/v1/models` said `inferhub-community`, the chat without
the header got the 403, and with it the group answered.

## Not established, said out loud

- **Nothing hides a prompt from the machines in a community group.** That is the deal, by the user's decision. The header
  and the two configuration keys make it a choice, not a protection.
- **Organisations were not set up in the lab.** Peering across organisations is covered by the coordinator's tests, not a
  live run.
- **No client library sends the header yet.** A client adds it by hand.
- **No reputation, limits per owner, or payment.** A community member is a machine whose owner opted in, nothing more.
- **Machines behind home routers** are not reachable yet (the next phase), and the console was read as JSON, not looked at
  in a browser.

## Upgrading

Nothing to do. A node without `Community` stays private; a coordinator without a `Mode: community` group asks no client for
anything.
