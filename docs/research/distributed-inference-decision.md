# Should InferHub change its shard engine? Measured, and the answer is: not now (phase 141)

**Decision: defer.** Keep llama.cpp's RPC backend as the engine of a split-model group. Reject Prima.cpp, Nakshatra and
EXO as engines to adopt. Keep one idea on the shelf with its numbers attached — a ring instead of a star — because it is the
only thing here that moved a measurement, it moved it only on a wide network, and it costs a patched engine.

What was read in each project's source, and why two of the three were not run:
[distributed-inference-engines.md](distributed-inference-engines.md). The evidence of the run below:
[runs/2026-11-15/](runs/2026-11-15/summary.md).

## How it was measured

```sh
LAB_MODEL_DIR=D:/models COMPARE_REVISION=fb07a72 deploy/lab/shards/compare-engines.sh
```

One GGUF (Llama 3.1 8B Q4, sha256 `87048bcd5521…`), two fixed prompts (9 words and 382 words), 48 tokens at temperature 0,
three repetitions at each of 0, 10 and 30 ms of round trip (`tc netem`, delay only), every container on 4 CPUs of one 64-core
host, one client clock for every engine. Five configurations:

| | What it is |
|---|---|
| whole | llama.cpp `b12182`, the model in one container — the text a llama.cpp split must give |
| rpc-star | llama.cpp RPC bare: a head holding no layers and three remote workers |
| **inferhub-group** | **the baseline**: the published InferHub 3.100.0 images (revision `fb07a72`), three nodes through their mTLS tunnels, asked through the coordinator. The head is a member, so a third of the layers never leave it |
| prima-whole | Prima.cpp `288df6a`, the model in one container — the text a Prima.cpp ring must give |
| prima-ring | Prima.cpp, four ranks of eight layers, each reading the file itself |

**Each engine is read against its own whole-model run**, never one engine's tokens per second against the other's: Prima.cpp
is a 2024 llama.cpp compiled with `-march=native`, the lab's llama.cpp is not, and their single-container speeds differ before
any network is involved (prefill 21 against 30 tokens a second).

## What the run found

Run twice on 2026-11-15; the second run is the one recorded, the first agreed with it within the ranges shown.

| Round trip | rpc-star | inferhub-group | prima-ring | Ring against the group, short / long prompt |
|---|---:|---:|---:|---:|
| 0 ms | 6.40 tok/s · 96 % of whole | 6.18 · 93 % | 6.55 · 101 % | 1.09× / 1.10× |
| 10 ms | 5.31 · 80 % | 5.44 · 82 % | 5.79 · 89 % | 1.10× / 1.08× |
| 30 ms | 2.73 · 41 % | 3.15 · 47 % | 4.17 · 64 % | **1.50× / 1.17×** |

| | rpc-star | inferhub-group | prima-ring |
|---|---:|---:|---:|
| Bytes between machines per generated token (short prompt) | 690 KB | 600–695 KB | **80 KB** |
| Bytes between machines before the first answer | 4.2 GiB | 3.0 GiB | **0** |
| Until it answers, containers started cold | 62 s | 85 s (with the hub's start and four enrollments) | 15 s |
| First token of the 382-word prompt, 0 ms | 19.4 s | 20.8 s | 20.4 s (its own whole model: 20.4 s) |
| First token of the 382-word prompt, 30 ms | 21.5 s | 23.7 s | 21.5 s |
| The text | the whole model's, every repetition | the whole model's, every repetition | its own whole model's, every repetition |
| The client hangs up | computing stops | not run | computing stops |
| A worker is killed | the head stays up and the next request gets no answer | the group reassembles on its spare (phase 137, checked by `verify-lab.sh`) | rank 0 stays up and the next request hangs past 60 s |

Read plainly:

1. **On a local network nothing here is faster.** At 0 and 10 ms the ring is 8–10 % ahead of the group, inside what two
   prompts and three repetitions can tell apart. A LAN group has no reason to change engine.
2. **At 30 ms the ring loses less, and by how much depends on the prompt.** A token costs the group about five round trips
   (312 ms against 160 ms unshaped, with two remote members) and the ring two (211 ms against 151 ms on the short prompt: four
   one-way hops for four ranks). On the long prompt the ring's lead falls to 1.17× and **why is not established**.
3. **The ring's larger difference is traffic, not speed.** Eight to nine times fewer bytes per token, and no weights crossing
   the network at load. On a home uplink of 10–20 Mbit/s the star's 5–6 Mbit per token is itself a ceiling of two to three
   tokens a second, whatever the latency; netem here shaped delay only, so **that ceiling is computed, not measured.**
4. **Time to the first token is prefill, and no engine here changes it.** 14 s whole, 19–21 s split, for every engine. Splitting
   a model costs a third more prefill and a ring does not give it back.
5. **Putting the head in the group was the right call in phase 136.** The group keeps 47 % at 30 ms where the bare star keeps
   41 %, tunnels and TLS included.
6. **Neither engine survives a lost worker by itself.** What makes an InferHub group survive is InferHub (spares, the 503 with
   the reason, reassembly), and a ring engine would need all of it again.

## The decision, per kind of use

| Use | Decision | Why |
|---|---|---|
| One person, a group on a local network | **reject** a change | 1.09× at best, inside the noise |
| A group on a local network, larger model | **reject** a change | the same measurement; the engine is not what limits it |
| A group across the internet (community, 138–139) | **defer** — a ring is the idea to build if this use grows | 1.17–1.50× at 30 ms and 8× less traffic are real; the price is a patched llama.cpp per model family, tunnels rebuilt for a ring, and every survival rule of 137 again |
| Several requests at once | **not established** | every run here was one request at a time |
| Adopting Prima.cpp | **reject** | no Windows build, CUDA only, sockets with no authentication, a 2024 fork to keep beside the llama.cpp a node already runs |
| Adopting EXO | **reject** | MLX weights, Apple-first; its speed-up assumes a microsecond link |
| Adopting Nakshatra | **reject** as an engine, **keep as the design to read** | not built here; it is the ring on a current llama.cpp, and its patch per architecture is the cost named above |

**The rule the script applies**, so two runs cannot be read two ways: *adapt* only if the candidate keeps at least 1.25× the
baseline's share of whole-model speed on **every** prompt at some shaped round trip; *defer* if on one prompt only; *reject*
otherwise or if the candidate does not give its own whole model's text. This run: defer.

## A next phase, if one is wanted

Not a ring. The cheapest measured cost is the load: 3.0–4.2 GiB crosses the network before a group answers, and a
`ggml-rpc-server` keeps what it received (`-c`) only for the *next* load. A worker that already has the model file could be
given its layers from its own disk instead of from the head. **Whether llama.cpp's cache can be filled that way without a patch
is not established** — it is the first thing such a phase would have to read, and if the answer is no, the phase is not worth
having.

## What this cannot tell you

- **GPUs.** Every container shares one host's cores and memory bandwidth; no VRAM, no GPU utilisation, no mixed hardware —
  which is what Prima.cpp's scheduler and device profiling exist for. Its placement solver never ran here: the layers were
  given by hand, equal, as InferHub's are.
- **Real networks.** netem added delay and nothing else: no loss, no jitter, no bandwidth cap, no asymmetric uplink.
- **Models above 8B**, and Prima.cpp's central claim (a model larger than memory, read lazily from disk) with them.
- **Several requests at once**, speculative decoding, and more than four machines.
- **The candidate behind InferHub's boundary.** Prima.cpp was run bare, on a Docker network. Drain, retry, certificate
  revocation and the boundary between owners were not run against it, because it was never connected to InferHub; they hold for
  the baseline because `verify-lab.sh` runs them.
- **First token through the coordinator on a short prompt.** `llama-server` keeps earlier prompts and a coordinator offers no
  switch for it: the same request took 0.5 s and 22.6 s in one series. The long prompt carries each run's own first words for
  that reason; the short one does not, because it is the text check.
- **Nakshatra and EXO's speed.** Nothing here measures either.
