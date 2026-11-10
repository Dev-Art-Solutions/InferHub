# InferHub v3.96.0: one model across several nodes, answering like any other

Phase 136, the cut point of the sharded-models track: the first release in which a split model answers a prompt. In 3.95
nodes could reach each other's workers through their tunnels, but a person at a shell had to start the head. Now the
coordinator does it. The admin defines a group, the coordinator sends it to its head once every member is ready, the head
runs llama.cpp over its own worker and its tunnels, and the model answers under the group's name. A client asks for it like
any other model and never learns it is three machines.

```jsonc
// coordinator
"ShardGroups": { "llama-8b-split": { "Head": "shard-a", "Members": ["shard-b", "shard-c"], "Model": "llama31-8b.gguf", "Context": 4096 } }
// the head node
"Node": { "Shards": { "Enabled": true, "RamGiB": 3, "AllowHead": true, "ModelsDir": "/models" } }
```

```http
POST /v1/chat/completions   {"model": "llama-8b-split", "messages": [...]}
```

Nothing changes without `ShardGroups` on the coordinator and `AllowHead` on a node.

## What it does

- **Groups are configuration on the coordinator**, not state: `ShardGroups:<name>` names the head, the members (by name or
  id), the model file on the head and the context.
- **Ready means everyone is there.** The head and every member are connected, their workers run, their tunnels are open,
  every member is one of the head's peers (a certificate from this coordinator, the same organisation — 3.95), and all speak
  one llama.cpp RPC version. Then the head is sent the group. When that stops being true, the coordinator takes the group
  back and the head stops it.
- **`GET /api/admin/shards/groups`** says, per group, whether it is ready or the first thing missing (*member 'shard-c' is
  not connected*, *shard-b is not one of shard-a's peers*, *the members' llama.cpp RPC protocols differ*), and what the head
  reports.
- **The head runs `llama-server`** over its own worker first, then a tunnel door per member. Every layer sits on a worker,
  each worker's share in proportion to what its owner offered (`--tensor-split`). It is supervised like any engine the node
  starts, and counts as running when `/health` answers.
- **Being a head is a separate consent.** `Node:Shards:AllowHead` is off by default: offering memory is not agreeing to hold
  a model file and run its head. The coordinator sends a file **name**, looked up in `Node:Shards:ModelsDir` (default: the
  llama.cpp engine's models folder); a path, a separator or `..` is refused.
- **The model is a chat model on the head**, so the coordinator routes it like any other: the OpenAI and Ollama dialects,
  streaming, the catalogue. The console shows each group under *Shard tunnels*.

## Measured in the lab

`deploy/lab/shards/tunnel-lab.sh`, three nodes in Docker (4 CPUs, 4 GB, 3 GiB offered each), a coordinator built from this
tree with one group defined. Nobody started anything by hand.

- `POST /v1/chat/completions` on the coordinator for `llama-8b-split` answered from three nodes, with **the same text as the
  whole model** for the same chat request, character for character. 64 tokens in 9.8 s end to end. Streaming answered too.
- **Revoking shard-c** made the group not ready (*member 'shard-c' is not connected*). The head stopped the group, the model
  left `/v1/models`, and a request got `model 'llama-8b-split' not found`.

## The published images

`inferhub-coordinator:3.96.0` and `inferhub-node:3.96.0`, revision `9e7be1d`, run through `tunnel-lab.sh`: the group was
assembled, and the chat request answered with **the same text as the whole model**. The revoke step found the bug fixed in
v3.96.1 (see below).

## Found while building it

- **A head with a dead Ollama could not serve its group.** In the lab the head's own engine is an Ollama that is not there,
  so the node reports its backend unhealthy, and the coordinator refused the group's model: *every node holding model
  'llama-8b-split' reports an unhealthy inference backend*. A node that reports a group as running now serves that model
  whatever its own engine says.
- **A stopped group stayed in the catalogue.** When a node's own engine cannot list its models, the node deliberately leaves
  the coordinator's inventory alone (so a short outage does not erase it). That also kept a stopped group listed. Once a head
  has served a group, it reports its list even while its own engine is down: the groups it runs now, possibly none. Its own
  models come back with its engine.

## Not established, said out loud

- **A group does not survive a member leaving.** It stops, and starts again when the member is back. Keeping it answering is
  phase 137.
- **No fit check before starting.** llama.cpp refuses a model that does not fit, and the group's state carries its sentence;
  the 3.93 estimate is the planning tool.
- **Not across an active-active cluster**, no GPU in a group, no model above 8B through a group, no real WAN.
- **The console** was read as JSON, not looked at in a browser.

**Fixed in v3.96.1:** on the published 3.96.0 images, a group stopped after a member was revoked stayed in `/v1/models`. The
coordinator held the head's last model list because the head's own engine was down, and that list included the group.

## Upgrading

Nothing to do. Without `ShardGroups` the coordinator sends nothing; without `AllowHead` a node runs nothing it is sent. A
coordinator older than 3.96 sends no groups, and a node older than 3.96 ignores them.
