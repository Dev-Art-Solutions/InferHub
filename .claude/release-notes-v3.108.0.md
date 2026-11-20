# InferHub v3.108.0: a member of a group loads its layers from its own disk

Phase 143, the second of the shared-compute track (142–149). Until now the head of a group sent every member its share of the
model over the network on each first load: 24.5 GiB for 3.102's 70B, about 70 minutes at 50 Mbit/s. A member that already held the
file gained nothing from it. Now it reads its own layers from that file first, and the head sends it only what llama.cpp always
sends.

```sh
LAB_MODEL_DIR=lab-models LAB_MEMBER_DISK=1 LAB_SHAPE="delay 15ms rate 50mbit" LAB_WAIT=1500 deploy/lab/shards/tunnel-lab.sh
```

## The gate: read in llama.cpp, then measured

The track's index made this phase conditional: worth having only if `ggml-rpc-server`'s cache could be filled from a local file
without patching llama.cpp. Read at b12182 (`ggml-rpc.cpp`, `rpc-server.cpp`, `llama-model-loader.cpp`):

- For a weight tensor above 10 MiB the head first asks the worker `SET_TENSOR_HASH` with the FNV-1a 64 of the tensor's bytes. A
  worker running with `-c` answers from `<cache>/rpc/<hash as 16 hex digits>`, a file holding exactly those bytes, and the
  tensor never crosses.
- An RPC device reports no host buffer, so the loader sets each tensor whole, with the bytes as they are in the GGUF file.

So a member holding the file can write those files itself. **The gate passed.**

## What was measured

The 8B (Llama 3.1 Q4, 32 layers) over three nodes in Docker on one PC, 4 CPUs each, CPU only, built from source. Every run was
a first load: fresh containers, empty caches.

| | Members without the file | Members with the file |
|---|---:|---:|
| Sent to shard-b / shard-c | 1 287 / 1 581 MiB | 248 / 225 MiB |
| Until it answers, unshaped | 52 s | 45 s |
| Until it answers, 15 ms and 50 Mbit/s on every node | 582 s | 165 s |

- **The same text as the whole model**, character for character, in every run. The comparison was `llama-server` on the same
  file in one container, with the same request.
- 2 868 MiB crossed the network without the file and 473 MiB with it (−84 %).
- What still crosses is every tensor of 10 MiB or less, which llama.cpp always sends. In an 8B that includes `attn_q` and
  `attn_output` (9 MiB each). A 70B's are 38 MiB, so there mostly `attn_k` and `attn_v` remain: 3.102's cached reload sent about
  270 MiB a member.
- Filling the caches (1 039 and 1 355 MiB) is a read and a write on each member's own disk before the head starts.

## What changed in the product

- **The hub tells each member its place in the split** (`ShardSeeds`: the model's name, the group's weights, its index). The
  member reads its layers from its own copy of the file into its worker's cache.
- **The group waits for it.** A member that can fill a cache holds the group back until it has said whether it holds the file,
  and then until it is done. Meanwhile `why` says how far: *shard-b is filling its cache from its own copy of llama31-8b.gguf
  (512 of 1039 MiB)*.
- **`loads` on `GET /api/admin/shards/groups`** says per member where its layers came from: its own disk, or over the network and
  why. The console shows each member's cache under *Shard tunnels*.
- **A member without the file, without room for its share, or older than 3.108 loads over the network exactly as before.** The
  cache is keyed by content, so a wrong file can only miss. It cannot change an answer.
- The file is looked for in `Node:Shards:ModelsDir` (else the llama.cpp engine's `Serve:ModelsDir`), and one directory down,
  where a download from the hub (`POST /api/admin/nodes/{id}/huggingface`) puts it. The head's own lookup gained the same.
- **New key `Node:Shards:CacheDir`.** The worker is now always given its cache directory as `LLAMA_CACHE`. The default is where
  llama.cpp put it by itself, so a cache made before 3.108 stays where it was.
- Each tensor is copied into a `.part` file and renamed only when whole: `rpc-server` would answer from a half-written file of
  the right name. A filled assignment is recorded beside the cache and not read again.
- **Disk cost:** a member keeps its share of the model in its cache. `rpc-server` never evicts it, and neither does InferHub.

## What changed in the lab

`tunnel-lab.sh` takes `LAB_MEMBER_DISK=1` (every member gets the file too) and `LAB_SHAPE` (every node's link is shaped from its
start, so a first load crosses it). The 8B now lives in the `lab-models` volume beside the 70B.

## Tests

`ShardSeedPlanTests`: the FNV-1a vectors, exact sizes by type and shape (a Q6_K row that is not a multiple of 32), layers by
name. `ShardSeederTests`: a member writes exactly its layers' tensors, named and filled as the worker reads them; it does not read
the file again for a filled assignment; a missing file, a full disk and no cache are each said; a file one directory down is found.
`ShardGroupsTests`: the hold, the progress, a member without the file not holding the group, an answer for another split not
counting. Slice: Shared 473, Coordinator 1 101, Node 805, Mesh (console contract, shards) 9, all passed.

## Not established

- **The file reaching a member through the hub, in the lab.** The lab mounts the file on each member. Delivery is 98's download,
  through the relay and the supply of 122–124 on a sealed node, unchanged and covered by their own tests.
- **A 70B filled this way.** The mechanism is the 8B's, and 3.102's cached reload already sent ~270 MiB a member.
- **GPUs.** Whether a worker offering a card (`-d Vulkan0`) takes the same hash path was not run.
- **Other llama.cpp builds.** The threshold, the hash and the file name are b12182's code, not a promise of its protocol. A
  build that changes them makes members miss, which is slower but never wrong.
- **Real networks.** One PC: `tc netem` stands in for a link, as in the rest of the track.
