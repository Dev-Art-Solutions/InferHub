# Distributed-inference engines beside InferHub's shard backend (phase 141)

Read from source at the commits below on 2026-11-15, not from the projects' front pages. The question this file answers is
narrow: **which of these could be put beside InferHub's existing split-model path on the same weights, and what is the
mechanism in each that could matter to it.** The measurement is in
[distributed-inference-decision.md](distributed-inference-decision.md).

InferHub's own path (phases 133–140): a group's head runs `llama-server --rpc …`, every member runs llama.cpp's
`ggml-rpc-server` on loopback, and the nodes reach each other only through InferHub's mTLS tunnels (or the coordinator's
relay). The engine is llama.cpp's RPC backend; everything about who may join, who is revoked and who consented is InferHub's.

## The pins

| | llama.cpp RPC (the baseline) | Prima.cpp | Nakshatra | EXO |
|---|---|---|---|---|
| Repository | `ggml-org/llama.cpp` | `OpenCPIL/prima.cpp` | `fthrvi/nakshatra` | `exo-explore/exo` |
| Commit | `fd27b20` (tag `b12182`, the lab's build) | `288df6a` (2026-09-01) | `373a17d` (2026-11-08) | `a60b956` (2026-11-06) |
| Licence | MIT | MIT (ggml authors + prima.cpp authors) | MIT (Petals authors and collaborators) | Apache-2.0 |
| What it is | a backend inside llama.cpp | a fork of llama.cpp from 2024 (`gpt_params`, one `src/llama.cpp` of 20 000 lines) | Python workers and gateway around llama.cpp `b9992` + a 2 600-line patch series (`engine/BASE`, `engine/patches/`) | a Python + Rust application around MLX |
| Weights | GGUF, one file at the head | GGUF, the whole file readable on every device | GGUF, cut into one sub-GGUF per worker (`partial_gguf.py`) | MLX safetensors (`mlx-community/*`); no GGUF loader |
| Platforms | everything llama.cpp builds for | Linux, macOS, Android (Termux); **no Windows**; GPUs through CUDA only (its README, FAQ 5–6) | Linux, macOS; CUDA, ROCm, Vulkan, Metal, CPU | Apple silicon is the only tested tier (`PLATFORMS.md`); Linux runs on the CPU through `mlx-cpu`; Windows is "longer term" |
| How the model is split | by layers, weights assigned to devices with `--tensor-split` | by layers, a window per device (`-lw`), several turns of the ring per token allowed | by contiguous layer ranges, one per worker | by layers (`PipelineShardMetadata`) or tensor-parallel (`TensorShardMetadata`) |
| Topology per token | **a star**: the head sends each worker its input, asks it to compute and reads the result back, worker after worker (`RPC_CMD_SET_TENSOR`, `GRAPH_COMPUTE`, `GET_TENSOR` in `ggml/src/ggml-rpc/ggml-rpc.cpp`) | **a ring**: each device runs its window and pushes the hidden state to the next; the last pushes to the head (`llama_send_tensors`, `src/llama.cpp`) | **a chain**: a worker pushes the hidden state to the next, the answer unwinds back (`InferenceStep.chain`, `proto/nakshatra.proto`) | MLX's own distributed groups (`mx.distributed`, backends `ring` and `jaccl`, the latter RDMA over Thunderbolt) |
| Where the weights come from at load | the head sends them over RPC (a worker's `-c` cache keeps them for the next load) | each device maps its own copy (`mmap`, `--prefetch`) | each worker holds its sub-GGUF | each device downloads its own |
| Who decides the placement | the operator (`--tensor-split`); in InferHub, the coordinator (`ShardLayers.Split`) | a solver on the head (HiGHS, `common/common.cpp`) fed by a profile of every device (CPU, disk via `fio`, memory, GPU); it may drop a device | `placement.py`, `serve_planner.py` on the gateway | "topology-aware auto parallel" (`master/placement.py`) |
| Transport and trust | plain TCP, no authentication — its README says *never run the RPC server on an open network* | plain ZeroMQ push/pull sockets on two ports per rank (`tcp://*:…`), **no authentication, no encryption** (no CURVE or PLAIN anywhere in the tree) | gRPC; TLS with pinned keys when configured, `insecure_channel` otherwise (`scripts/worker.py`, `client.py`); its own identity, relay, admission and ledger stack beside it | Zenoh discovery on the local network; devices join by cluster name and build |
| Failure handling seen in source | a lost worker aborts the head | `llama_rebuild_topo`, a 1 s receive timeout on meta messages, `/v1/cancel` | `step_id` as an idempotency key, per-worker KV truncation | not read |

## What each one means for InferHub

**llama.cpp RPC — stays the engine, and the lab measured why it is slow on a wide network.** Phase 133 counted nine to
thirteen round trips a token with three workers. That is the star: per worker, per graph, a send, a compute and a read, each
waiting on the last. The build InferHub pins also has commands the lab has never used (`RPC_CMD_GRAPH_RECOMPUTE`,
`RPC_CMD_COMM_*`); what they change per token on a newer build is **not established** here.

**Prima.cpp — the one candidate that runs on the lab's weights as they are, so it is the one measured.** Same GGUF, same
quantization, CPU build, a `llama-server` with the same `/completion`. Its claim worth testing is not the headline ("15×
faster" is against llama.cpp swapping a 70B model to disk on one machine — a memory claim, not a network one); it is the ring:
one hidden state across each boundary per token instead of a conversation with every worker. What rules it out as an engine
InferHub could *ship* is in the table and none of it is measurement: no Windows build (InferHub ships a Windows
node with its own setup, phase 101), CUDA only, a 2024 fork that is not the llama.cpp a node already runs (a second engine to pin, patch and supervise),
and sockets with no authentication — which InferHub's tunnels could wrap, as they wrap RPC, but the ring means every rank
dials the next *and* the head, so the tunnel's forward-per-peer shape would have to be rebuilt around it.

**Nakshatra — the same mechanism as Prima.cpp's ring, on a current llama.cpp, and the closest to what InferHub would build.**
A worker loads only its layer range from a sub-GGUF, takes a hidden state, returns a hidden state; five source files of
llama.cpp are touched (`llama-graph.cpp`, `llama-model-loader.cpp`, `llama-model.cpp`, `llama-model.h`, `models/llama.cpp`,
plus Qwen3). It was **not built in this phase**: it needs its patched engine compiled, the model cut per worker and a Python
gateway per request, and its topology is the one Prima.cpp already lets the lab measure. Marked `SKIP` in the run with that
reason; nothing here is a claim about its speed. Two things in its source are worth a later look and are *not* evidence of
anything yet: the patch is small where it touches llama.cpp, and it supports three architectures only (llama, qwen3,
qwen3moe) — a partial-load patch has to be written per model family, which is the maintenance bill of this approach.

**EXO — not comparable on these weights, and aimed at a different machine.** Its only inference engine is MLX
(`src/exo/worker/engines/mlx`); it loads MLX-format checkpoints, so there is no run of *this* GGUF at *this* quantization to
put beside the baseline, and a comparison across formats would be comparing quantizers. Its speed-up claim (tensor parallelism,
"1.8× on two devices") is measured on Mac Studios joined by RDMA over Thunderbolt — a link with microseconds of latency. Phase
133 already rejected tensor parallelism across a wide network for the reason that claim depends on (a synchronisation per
layer), and nothing in EXO's source argues otherwise for a 30 ms link. Marked `SKIP`. What it does that InferHub does not —
discovery, placement by measured link — InferHub does through its coordinator.

## Shortlist

| Candidate | Verdict | Why |
|---|---|---|
| llama.cpp RPC | baseline | what a group runs today |
| Prima.cpp | **measured** as a mechanism; not adoptable as an engine | reads the same GGUF; no Windows, no authentication, a 2024 fork |
| Nakshatra | not built; the design to read if a ring is ever built | same mechanism as the one measured, on current llama.cpp, at the price of a patch per model family |
| EXO | incompatible | MLX weights, Apple-first, tensor parallelism that assumes a microsecond link |

No code, patch or dependency from any of the three entered InferHub. All three licences (MIT, MIT, Apache-2.0) would permit
it; that is recorded so a later phase does not have to re-read them, not because anything was taken.
