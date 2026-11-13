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

## Through the tunnels (phase 135)

`tunnel-lab.sh` runs a coordinator with node certificates on and three nodes enrolled with one-time tokens, each offering
3 GiB. Their workers bind loopback; each node reaches the others only through its tunnel (mTLS, the peers the hub listed),
by a local forward per peer. A head in shard-a splits the 8B over its own worker and its two forwards.

| Setup (Llama 3.1 8B Q4, 4 CPUs per node) | Round trip a↔b | Generate | Text |
|---|---:|---:|---|
| whole model, one container | — | 6.71 tok/s | — |
| through the tunnels, unshaped | 0.25–0.39 ms | **6.29 tok/s** | the same, character for character |
| through the tunnels, `internet` on all three | 15.4 ms one link shaped; 30 ms both | 2.93 tok/s | the same |
| 133, plain RPC, unshaped / `internet` | 0.07 / 30 ms | 6.42 / 2.29 tok/s | the same |

- **TLS costs about 2 %** (6.29 against 133's 6.42). The `internet` run is faster than 133's because the head here sits
  beside one of the workers: a third of the layers never cross the network.
- **The matrix follows the network:** with only shard-c shaped (15 ms one way), a↔b stayed at 0.3 ms and every pair with c
  read 15.4 ms.
- **Revoke closes the door:** revoking shard-b's certificate dropped it from both other lists within seconds; shard-a then
  refused its connections by name (*certificate 0d6189fd0774… is not one the coordinator listed*). The head that was using
  shard-b's worker died with it — llama.cpp aborts on a lost worker (137 handles that).
- **Set `Node:Shards:Threads`** in a CPU-limited container: without it the worker ran llama.cpp's default thread count for
  the whole host, and the split reached 2.5 tok/s instead of 6.3.

## A group the coordinator assembles (phase 136)

`tunnel-lab.sh` now defines one group on the hub (`ShardGroups__llama-8b-split__…`: head shard-a, members shard-b and shard-c,
`llama31-8b.gguf`, context 2048) and lets shard-a be a head (`Node__Shards__AllowHead=true`, `ModelsDir=/models`). Nobody starts
anything by hand: the hub sends the group once all three are ready, shard-a runs `llama-server` over its worker and its two
forwards, and the script asks the hub's `/v1/chat/completions` for `llama-8b-split`.

- **The same text as the whole model** for the same chat request (a `llama-server` with the whole file, the same `--alias`):
  64 tokens in 9.8 s through the hub, end to end; streaming answers.
- **Revoking shard-c:** `/api/admin/shards/groups` said *member 'shard-c' is not connected*, shard-a stopped the group, the model
  left `/v1/models`, and a request got `model 'llama-8b-split' not found`.
- **Found here:** shard-a's own engine (an Ollama that is not in the container) is down, so the node reports its backend
  unhealthy — the hub refused the group's model for it until a running group counted as serving on its own.

## A group that survives a member leaving (phase 137)

`tunnel-lab.sh` now starts four nodes — shard-d is the group's spare — with each node's local API on loopback (an owner's
pause). `survive-lab.sh` then:

- **revokes shard-c:** every request got `503`, `Retry-After: 15`, *group 'llama-8b-split' is reassembling: shard-d stands in
  for shard-c (member 'shard-c' is not connected)* for 57 s, then `200` with **the same text as before**; the layers moved from
  shard-c to shard-d (22–31 + output);
- **pauses shard-b through its own control API during a 200-token request:** the head showed `draining`, the request completed
  with its 200 tokens, and the next one got a 503 saying why.
- **Found here:** the pause was first a `500 could not write /app/node.local.json` — the node images now keep that file in `/data`.

## A community group (phase 138)

`community-lab.sh`: shard-a (the head), shard-b and shard-c opted into community groups with the risk accepted; shard-d offers
the same 3 GiB and did not; shard-s is sealed and asks to join anyway. One group, `Mode: community`, `Workers: 2`.

- The coordinator picked **shard-b and shard-c**, never shard-d.
- **shard-s exited at startup**, naming `Node:Shards:Community` as a door a sealed node does not open.
- `/v1/models` lists `llama-8b-community` as `owned_by: inferhub-community`.
- A chat without `X-InferHub-Community: accept` got `403` with the risk in words, on `/v1/chat/completions` and `/api/chat`;
  with the header, the answer.

## A node behind NAT (phase 139)

`relay-lab.sh` puts shard-c on a second network that only the coordinator is also attached to. Docker routes nothing between
two bridge networks, so shard-c can dial the coordinator and nobody can dial shard-c — what a home router gives. With the
coordinator's relay on (`ShardRelay__Enabled`) and `Node__Shards__TunnelRelay` on the three nodes, the community group runs
with shard-c in it: a↔b direct, every pair with shard-c by way of the relay.

Measured (2026-11-13, source-built 3.99.0, the 8B Q4, 3 GiB offered per node):

| Pair | Path | Round trip |
|---|---|---:|
| shard-a ↔ shard-b | direct | 0.13–0.24 ms |
| shard-a ↔ shard-c | relay | 0.35–0.60 ms |
| shard-b ↔ shard-c | relay | 0.49–0.72 ms |

- The group answered **the same text** as the same community group on direct tunnels, 64 tokens in 10.5 s end to end.
- The relay carried 1.73 GB in 29 connections — mostly the layers going to shard-c at load.
- Revoking shard-c left no pair at the relay and refused its three attempts to wait again; the next request got a 503
  saying the group has too few members.

**Not established here:** a real router (symmetric NAT, idle timeouts, loss), and the relay's cost on a real uplink — in the
lab the coordinator shares the host with the nodes, so the extra hop costs almost nothing.

## Verification day (phase 140)

```sh
LAB_MODEL_DIR=D:/models VERIFY_REVISION=<commit> ./verify-lab.sh 3.99.0
```

Pulls the published coordinator and node of that version, checks what they say they are, builds the lab node on the published
one, runs `tunnel-lab.sh`, `survive-lab.sh`, `community-lab.sh` and `relay-lab.sh`, and prints PASS or FAIL per claim (exit 0
only when all held). Transcripts stay in `$LAB_OUT` (default `C:/tmp`) as `verify-*.txt`.

Run on the published 3.99.0 images (2026-11-13, revision `024da81`): **28 checks, all held** — the private group, a member
leaving and a drain, the community pick and the client's yes, and the relay.
