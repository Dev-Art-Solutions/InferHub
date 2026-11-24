# InferHub v3.112.0: several requests at once on a group

Phase 147, the sixth of the shared-compute track (142–149). A group's head runs one `llama-server`, and llama.cpp puts every request it
is running into the same pass across the members. This release measured how much that buys. It also made the coordinator count a group
by what the group's engine runs. Before, it read the head's own engine's count, and a default Ollama said 1.

```json
"ShardGroups": { "llama-8b-split": { "Head": "shard-a", "Members": ["shard-b", "shard-c"],
                                     "Model": "llama31-8b.gguf", "Slots": 8 } }
```

## What changed

- **The head reports each group's slots as its engine answered them** (`/props` → `total_slots`, asked once the group answers
  `/health`). The group's name now carries `slots` in the node's model list. The hub's slot routing, `Queue:SlotWaitSeconds` (110) and
  `Priority` (126) read that count instead of the node's `ModelSlots`.
- **`ShardGroups:<name>:Slots`** (default 0 = llama.cpp's own count, four at b12182): the head passes `-np N -kvu`, at most 64.
  `-kvu` keeps one context shared by every slot. Without it, a given count cuts `-c` into equal parts (measured: 4096 → 1024 a slot).
- `GET /api/admin/shards/groups` shows `slots` for a running group. `ShardGroupSpec.slots` and `ShardGroupState.slots` are additive
  and absent when unset; an older head ignores `slots`.
- **The lab:** `concurrency.sh` + `compose.parallel.yaml` + `compare/burst.mjs` (the bare engine, K requests sent together),
  `group-concurrency.sh` (through the hub), `tunnel-lab.sh` `LAB_SLOTS` / `LAB_SLOT_WAIT`.

Zero new packages; `InferHub.Shared.csproj` still empty; nothing new persisted on the coordinator.

## What was measured

The 8B (Llama 3.1 Q4) over three 2.5 GiB workers, llama.cpp b12182, CPU only, 4 CPUs per container, one PC. 48 tokens, the same prompt,
temperature 0, two repetitions, median. Tokens per second across all the requests sent together.

| `-np 4` | One | Two together | Four together |
|---|---:|---:|---:|
| 0 ms | 6.11 | 9.88 (1.62×) | 13.56 (2.22×) |
| 30 ms | 2.77 | 4.58 (1.66×) | 4.96 (1.79×) |
| 120 ms | 0.60 | 0.98 (1.63×) | 1.42 (2.35×) |

With `-np 1` the same bursts gave 1.00–1.19×. Four at 120 ms ended at 319 s against 132 s with four slots. Every run kept a single
request's text.

**Through the hub** (`tunnel-lab.sh`, `Queue:SlotWaitSeconds=20`, four chats at once, 0 ms):

| | Four chats end | First tokens | Hub held |
|---|---:|---|---|
| v3.111.0, published images | 28.5 s | 0.2 / 7.9 / 15.7 / 20.3 s | waited 6, sent anyway 2 |
| v3.112.0, built from source | 14.8–15.8 s | all within 3.2 s | none |

The group's state said `slots: 4` and the hub's `slotsByModel` the same. With `Slots=2` the head ran `-np 2 -kvu` and the hub saw 2.

## Not established, said out loud

- **The round trip is not paid once for all of them.** At 30 ms a pass of four cost 0.75 s against 0.33 s for one, more than the
  compute difference at 0 ms. What grows with the batch on the network is not established.
- **Each request is slower than alone** (3.9 tok/s each in a burst of four at 0 ms, against 6.1 alone). Slots trade one caller's speed
  for everyone's.
- **With `Slots=2` and four chats, two answered with a different temperature-0 text.** That was a batch of two with a reused prompt;
  llama.cpp does not promise the same numbers across batch shapes. Every four-slot run kept the text.
- **Found, not changed:** in that run the hub held none of the four. All saw a free slot before the first was counted (110's check is not
  atomic for a burst), and two queued in the engine instead. For a group, with one head, the caller sees about the same.
- GPUs, a real network (netem delay only), a 70B with several slots (its KV pool is the head's memory), and slots together with a draft (146).

## Checks

- `dotnet test InferHub.sln`: 3 014 passed, 70 skipped, 0 failed.
