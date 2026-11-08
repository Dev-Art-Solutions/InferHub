# The shard lab (phase 133)

One model split by layers across containers that are each too small to hold it — llama.cpp's RPC backend, a head
(`llama-server --rpc …`) and three workers (`ggml-rpc-server`), on one bridge network shaped by `tc netem`. It is
how the sharded-models track (133–140) is proven without a cluster: **it shows the mechanism and what latency costs,
not what real cards on real networks do.**

```sh
docker compose build                                   # llama.cpp b12182 with GGML_RPC=ON, CPU only (~5 min)
LAB_MODEL_DIR=D:/models docker compose up -d           # $LAB_MODEL (default llama31-8b.gguf) from that directory
./measure.sh                                           # through the split head, temperature 0
./netem.sh internet && ./measure.sh                    # lan | internet | far | off
LAB_MODEL_DIR=D:/models docker compose --profile whole up -d whole && ./measure.sh 18081   # the whole model, for comparison
```

Knobs (environment): `LAB_MODEL`, `LAB_WORKER_MEMORY` / `LAB_HEAD_MEMORY` (2560m — below an 8B Q4's 4.4 GiB, so no
container holds it alone), `LAB_SPLIT` (`1,1,1`), `LAB_CONTEXT`, `LAB_THREADS`, `LAB_CPUS`.

**Why a build of our own:** the official `ghcr.io/ggml-org/llama.cpp` images are built without `GGML_RPC` (checked on
b12182). **Every container runs the same build** — llama.cpp's RPC refuses a peer of another protocol version.

**`0.0.0.0` on a worker is the lab only.** llama.cpp RPC has no authentication; an InferHub node binds it to loopback
and reaches it through its own tunnel (track invariant, phase 135).

## Measured (2026-11-08, phase 133)

Llama 3.1 8B Q4 (4.4 GiB), 64-core host, 4 CPUs and 4 threads per container, 3 workers at 2.5 GiB each, 32–64 tokens:

| Network | Round trip | Generate | Prompt |
|---|---:|---:|---:|
| whole model, one container | — | 6.70 tok/s | 22.0 tok/s |
| split, unshaped | 0.07 ms | 6.42 tok/s | 17.1 tok/s |
| split, `lan` | 0.7 ms | 6.05 tok/s | 19.1 tok/s |
| split, `internet` | 30 ms | 2.29 tok/s | 6.7 tok/s |
| split, `far` | 120 ms | 0.59 tok/s | 2.4 tok/s |

- **The split answers the same text, character for character**, as the whole model at temperature 0, on every profile.
- **A token costs nine to thirteen round trips with three workers** (437 ms a token at 30 ms and 1695 ms at 120 ms,
  against 156 ms unshaped): RPC is a star — the head
  sends each worker its input, asks it to compute and reads the result back, worker after worker. Fewer, larger members
  are faster than many small ones, and the round trip, not the bandwidth, is what the internet costs.
- **Loading sends the weights from the head.** With the workers' cache (`-c`, 1.1 GiB each kept on the worker) a reload
  over `internet` took 180 s; a cold load at 50 Mbit/s would be roughly 12 minutes (computed, not measured).

**Not established here:** GPUs (the lab is CPU only, every container shares one host's cores and memory bandwidth);
real home networks (NAT, jitter, loss — netem adds none); models above 8B; more than three workers.

## A node's own worker (phase 134)

`node.Dockerfile` is the published node image with this lab's llama.cpp copied to `/opt/llama`, so the node's shard
worker (`Node:Shards`) has a `ggml-rpc-server` to supervise. The worker binds `127.0.0.1` inside the container, so a
head reaches it only from the same container until 135's tunnel:

```bash
docker build -f node.Dockerfile --build-arg NODE=3.94.0 -t inferhub-lab-node:3.94.0 .
docker run -d --name shard-a -e Coordinator__Url=http://host.docker.internal:5080/ -e Coordinator__EnrollmentSecret=… \
  -e Node__Shards__Enabled=true -e Node__Shards__RamGiB=2 -v "$LAB_MODEL_DIR:/models:ro" inferhub-lab-node:3.94.0
docker exec shard-a /opt/llama/llama-server -m /models/<model>.gguf --rpc 127.0.0.1:50052 --device RPC0 -ngl 999 --port 18080
```

The hub's `/api/admin/nodes` then shows `shards: {state: running, protocol: "8.0.1", …}` for that node.
