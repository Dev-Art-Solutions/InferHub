# InferHub v3.109.0: the coordinator places a group itself

Phase 144, the third of the shared-compute track (142–149). Until now an admin wrote every group by hand: the head, the members,
the spares. The coordinator already knew what that person had to look up — what each node offers, which node reaches which and how
fast — except which node holds which file. Nodes now report that, and a group can name only the model:

```json
"ShardGroups": { "llama-8b-auto": { "Mode": "auto", "Model": "llama31-8b.gguf", "Context": 2048 } }
```

```sh
LAB_MODEL_DIR=lab-models LAB_AUTO=1 LAB_SLOW="b c" LAB_MEMBER_DISK=1 LAB_GROUP=llama-8b-auto LAB_WAIT=600 deploy/lab/shards/tunnel-lab.sh
```

## How it chooses

- **The head** is a node that holds the file in its `Node:Shards:ModelsDir` and whose owner set `Node:Shards:AllowHead`. Nodes
  report their GGUF files (name, size, layers, KV per token, from the header) and whether they may head.
- **The members** are the head's peers on its RPC protocol whose offers, with the head's, hold the model at the context. Each
  worker's share of the layers is proportional to its offer, so that is exact: weights + KV ≤ Σ offers × (1 − reserve ÷ the
  smallest offer).
- **Among those, the smallest sum of the head's round trips to them wins** (compared to 0.1 ms), then fewer members, then members
  that hold the file themselves (they load from their own disk, 3.108). Every token visits each member and comes back, in turn,
  so the round trips add up — 141's measurement. Every node that may head is tried as head.
- **A placement is kept while it can run.** A better round trip never reloads a running group (a reload is minutes). When a node
  in it cannot take part — gone, cordoned, paused, worker stopped — it is placed again and says why: *placed again: shard-d is
  cordoned*. `POST /api/admin/shards/groups/{name}/replace` does it on purpose.
- **It waits for the fleet to hold still** — the candidates, their files and offers, who may reach whom — for `SettleSeconds`
  (30), and for the head to have measured a round trip to every peer.
- `Head` pins the head; `Members` limits the pool. A group without `Mode: auto` is unchanged.

`GET /api/admin/shards/groups` has `mode` and `placement[]`; the console shows each node's group files and whether it may head,
and the auto groups with their placement and a *Place again* button.

## What was measured

The 8B (Llama 3.1 Q4) over four nodes in Docker on one PC, 4 CPUs and a 3 GiB offer each, CPU only, built from source. Only
shard-a may head; every node holds the file; shard-b and shard-c were put 20 ms away with netem.

| | Placed on | Round trip from shard-a | 64 tokens |
|---|---|---:|---:|
| All four up | shard-d (b and c left out) | 0.30 ms | 9.9 s |
| shard-d stopped | shard-c, running 52 s after the stop | 20 ms | 11.8–12.3 s |

- One member is enough: a + one 3 GiB node hold the 8B at 2 048 tokens (5.6 of 6 GiB).
- Containers started to the first answer: 80 s, 30 of them settling.
- **The same text** through every placement (b, d, c).

**Found by the lab, fixed before the release:** the first run placed the group on shard-a and shard-b — the first two nodes to
connect — and kept it there, 20 ms apart, before shard-d had enrolled. Hence the settling. And the reason a group moved was lost in
the 30 s of settling that followed a break; it is now kept until the new placement says it.

**Also fixed:** a node certificate issued on the call that created the coordinator's CA could start one second before its issuer
and be refused (`notBefore` earlier than the issuer's). Seen as a rare test failure; it is clamped to the issuer's now.

## What was not established

- **A real network.** Every round trip here is netem on one PC.
- **That the sum of round trips predicts speed in general.** It is 141's model; this release compared one near and one far
  placement (9.9 s against 11.8–12.3 s), not every choice it can make.
- More than four nodes, the 12-nearest cut, GPUs, and a 70B placed this way.
- A fleet whose nodes come and go faster than `SettleSeconds`: it would wait as long as they keep changing.

## Tests

Suite green except two `ChatGptBackendTests` that fail on this machine only (a path that resolves under the repository; green on
CI) and a `VoiceCatalogueTests` case that failed once under the full suite's load and passes alone. New: `ShardPlacementTests` (11),
`ShardGroupsTests` auto cases (7), `ShardModelScannerTests` (3).

## Published-image check (2026-11-22)

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.109.0` (revision label `22c5a21`) and `inferhub-node:3.109.0` with the lab's
llama.cpp on top, the same lab (`HUB_IMAGE=… NODE_IMAGE=inferhub-lab-node:3.109.0 LAB_AUTO=1 LAB_SLOW="b c" LAB_MEMBER_DISK=1`):

- Settled, placed on **shard-d (0.23 ms)**, b and c left out at 20 ms; shard-d loaded from its own disk; the same text. 79 s from
  containers started to the answer.
- `docker stop shard-d`: running on **shard-c 57 s later**, `placement[0]` = *placed again: a member left*.
- `POST …/replace`: placed again at once, *placed again: an admin asked (…)*.
