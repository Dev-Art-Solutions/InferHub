# InferHub v3.93.0: a model no node holds, across several

Phase 133, the first of the sharded-models track. Until now, *Check fit* asked one node at a time. A model that fit on
no single node was simply "does not fit". Now the hub asks every node that allows it. It answers with which nodes
*together* would hold a GGUF, and which layers each one would take.

```http
POST /api/admin/fit/group   {"link": "unsloth/DeepSeek-V3.1-GGUF", "context": 8192}
```

This release only estimates. Nothing is installed or started. Running a split model comes in later phases of the track.
Today's way of working is unchanged: a node with its own models, each routed whole, is still the default.

## What it does

- **One node reads the model, the others report their memory.** The first node that can reads the model through its own
  estimate (118). Every other node is asked only for its machine facts, through `machine`, a new management operation.
  Asking for machine facts reads no model and makes no network call. Only nodes whose owner set
  `Node:Management:AllowFromHub` are asked. Every other node appears in `asked` with the reason. That includes a node
  older than 3.93.
- **Whole layers, in proportion.** llama.cpp splits a model by layers, so the plan does too. Each layer costs its weights
  and its share of the KV cache at the chosen context. Each member also keeps 118's working reserve. A member offers its
  usable card plus usable RAM, within its owner's limits. Members are taken largest first until the layers fit. The
  layers are then spread in proportion to what each member offers, and no member gets more than it can hold.
- **Alone beats a group.** If one node holds the model alone, that is the answer. "Alone" is the same 118 estimate that
  *Check fit* shows for llama.cpp on that node, so the two never disagree.
- **The head is the member with disk space for the file**, because llama.cpp RPC loads the model on the head and sends
  the weights to the others.
- **`why` says what a split costs.** Every token visits each worker in turn, so the round trip between nodes sets the
  speed, not the bandwidth. A member whose layers do not fit its card runs them on its CPU, and the group waits for it.
- **In the console:** *Model management → Models that fit → Across nodes*. You get one row per variant: whether it fits
  one node alone, needs *N nodes together*, or fits nowhere. Each row lists the members with their layer ranges and the
  reasons.

## The shard lab

`deploy/lab/shards/` runs llama.cpp's RPC backend in Docker. A head (`llama-server --rpc`) and three workers
(`ggml-rpc-server`) each have a memory limit below the model's size. `tc netem` shapes the network between them. The
official llama.cpp images are built without RPC, so the lab builds its own (b12182, `GGML_RPC=ON`, CPU).

Llama 3.1 8B Q4, three workers at 2.5 GiB each, 4 CPUs per container:

| Network | Round trip | Generate |
|---|---:|---:|
| whole model, one container | — | 6.70 tok/s |
| split, unshaped | 0.07 ms | 6.42 tok/s |
| split, LAN | 0.7 ms | 6.05 tok/s |
| split, internet | 30 ms | 2.29 tok/s |
| split, another continent | 120 ms | 0.59 tok/s |

- At temperature 0, **the split model wrote the same text as the whole model, character for character**, on every
  profile.
- **A token costs nine to thirteen round trips** with three workers. RPC is a star: the head sends each worker its input,
  waits for the computation and reads the result, one worker after another. Fewer, larger members are faster.
- With the workers' cache (`-c`), a reload over the internet profile took 180 s.
- **The track's gate passed.** The internet profile is well above 1 tok/s, so the track continues.

## Found while building it

- **Counting only the card made a group useless.** The first version counted only a member's card. Asked live, each of
  the two nodes (256 GB of RAM each) offered 23 GiB, so a group could never hold a model one of them could not hold
  alone. On paper the same goes for a smaller box: a 16 GB card with 32 GB of RAM holds more by itself (118 adds the
  two) than three such cards do together. A member now offers its card and its RAM, as an `rpc-server` can.
- **A group of one.** A node with the memory for every layer, whose own estimate says no (because of its disk), came out
  as a one-member group. That is now "does not fit", with the node's own reason.

## Verified

- Live from source: a hub and two nodes on this box (24 GB card, 256 GB RAM each, as reported). For DeepSeek V3.1:
  the 1- and 2-bit quants fit one node alone (experts in RAM). Q3 through Q5 fit **lab-a + lab-b together** (layers
  0–30 and 31–60). Q6 and above fit nowhere: "3 short, about 25 GiB more". The console's *Across nodes* rendered all of
  this in headless Edge.
- The four test slices, including `GroupFitTests` (10), `GroupFitServiceTests` (4), the node's `machine` operation and
  the group endpoint over a real hub and node (`ManagementMeshTests`).
- A deployment that changes no configuration behaves the same: there is one new management operation and one new
  endpoint, both read-only. Zero new packages; `InferHub.Shared.csproj` is still empty.

## Not established

- **An `rpc-server` offering a card and the CPU together.** The lab is CPU only. The capacity a member offers assumes
  both devices.
- **GPUs in a group at all**, real home networks (NAT, jitter, loss; netem adds none), models above 8B through RPC, and
  more than three workers.
- **Memory reserved for groups.** The estimate counts each member's whole usable memory. Phase 134 lets a node offer a
  slice of it.
- The two live nodes were on one machine, so they reported identical hardware. A mixed fleet was covered only by the
  unit tests.
