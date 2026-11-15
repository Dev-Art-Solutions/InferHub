# InferHub v3.101.0: the engine under a split model, measured against other engines — and it stays

Phase 141. InferHub runs one model across several machines on llama.cpp's RPC backend (133–140). Other projects split
models too, and some claim to be much faster. This release does not change the engine. It adds the command that measures
the question, the reading of three other engines at pinned commits, and a written decision.

```sh
docker build -f deploy/lab/shards/candidates/prima.Dockerfile -t inferhub-lab-prima:288df6a deploy/lab/shards/candidates
LAB_MODEL_DIR=D:/models COMPARE_REVISION=<commit> deploy/lab/shards/compare-engines.sh
```

**This release changes no product code.** The 3.101.0 images differ from 3.100.0 only in their version.

## What was added

- **`deploy/lab/shards/compare-engines.sh`** — one GGUF, two fixed prompts, 48 tokens at temperature 0, three repetitions
  at 0, 10 and 30 ms of round trip, through five configurations: the whole model, bare llama.cpp RPC, a group on the
  **published** InferHub images, Prima.cpp whole and a Prima.cpp ring. One client (`compare/ask.mjs`) times every engine.
  `compare/report.mjs` writes the tables, prints PASS or FAIL per gate and a decision, and exits 0 only when every gate held.
- **`deploy/lab/shards/candidates/prima.Dockerfile`** — Prima.cpp at commit `288df6a`, CPU only. A lab fixture, like the
  lab's llama.cpp.
- **`docs/research/distributed-inference-engines.md`** — Prima.cpp, Nakshatra, EXO and llama.cpp RPC read from source at
  pinned commits: licence, weights, platforms, how each splits a model, what crosses the network per token, who it trusts.
- **`docs/research/distributed-inference-decision.md`** — the measurement, the decision per kind of use, and what a lab
  on one PC cannot show. The run's files are in `docs/research/runs/2026-11-15/`.

## The run: published 3.100.0 (revision `fb07a72`), 72 timed requests, 4 gates of 4

64-core host, Docker Desktop, Llama 3.1 8B Q4 (sha256 `87048bcd5521…`), 4 CPUs a container. Each engine is read against
its own whole-model run, because the two builds of llama.cpp differ before any network is involved.

| Round trip | llama.cpp RPC, bare | InferHub group | Prima.cpp ring |
|---|---:|---:|---:|
| 0 ms | 6.40 tok/s · 96 % of whole | 6.18 · 93 % | 6.55 · 101 % |
| 10 ms | 5.31 · 80 % | 5.44 · 82 % | 5.79 · 89 % |
| 30 ms | 2.73 · 41 % | 3.15 · 47 % | 4.17 · 64 % |

- **Every split gave its own whole model's text**, on every repetition.
- **On a local network the ring is 8–10 % ahead of the group** — inside what this run can tell apart.
- **At 30 ms the ring keeps 1.50× the group's share on the short prompt and 1.17× on the long one.** A token costs the group
  about five round trips and the ring two. Why the lead shrinks on the long prompt was not established.
- **The ring sends 80 KB between machines per token; the star sends 600–700 KB.** At load the star sends 3–4 GiB of weights
  and the ring sends none, because every machine reads the file itself.
- **The first token of a 382-word prompt takes 19–21 s on every split engine** (14 s whole). No engine here changes that.
- **A killed worker stops both engines answering.** What makes an InferHub group survive is InferHub (137), not the engine.
- **The head being a member (136) is worth six points at 30 ms**: 47 % for the group against 41 % for the bare star.

The run was done twice. The second is the one recorded; the first agreed with it within the ranges in the summary.

## The decision: defer

The engine stays llama.cpp RPC.

- **Prima.cpp is not adopted.** No Windows build, CUDA only, plain ZeroMQ sockets with no authentication, and a 2024 fork
  to keep beside the llama.cpp a node already runs.
- **EXO could not be measured on the same weights.** Its only engine is MLX and it loads MLX checkpoints, not GGUF.
- **Nakshatra was read and not built.** It is the same idea as Prima.cpp's ring on a current llama.cpp, at the price of a
  patch per model family.
- **A ring is kept as an idea, with its numbers.** It pays only across the internet, and it would need a patched engine,
  tunnels rebuilt for a ring, and every survival rule of 137 again.

The script applies a fixed rule: *adapt* needs 1.25× on every prompt at some shaped round trip. This run reached it on one
prompt of two, which is *defer*.

## What building it found

- **`llama-server` keeps earlier prompts, and a coordinator has no switch for it.** In the first full run a repeated long
  prompt through the coordinator answered its first token in 0.3 s instead of 20 s, and the script recorded that as the
  group's latency. The long prompt now starts with each run's own words. The short one does not, because it is the text
  check — so the short prompt's first token through a coordinator still varies (0.5 s and 22.6 s in one series) and is
  listed below as not established.
- **The first decision rule compared the ring with the bare star and said *adapt* (1.57×).** The baseline is the InferHub
  group, whose head holds a third of the layers; against it the lead is 1.17–1.50×. The rule now uses the group and asks
  for the lead on every prompt.
- **The brief was not in `plan/00-overview.md`**, which `ContextContractTests` requires. It is now.

## Not done from the brief

- **Nakshatra was not built and run.** The brief asked for at least one candidate, and Prima.cpp is that one.
- **Drain, retry, certificate revocation and the boundary between owners were not run against the candidate.** It was never
  connected to InferHub. They hold for the baseline because `verify-lab.sh` runs them. A cancel and a lost worker were run
  against both engines, bare.
- **No VRAM or GPU utilisation.** The lab has no GPU in it.
- **Several requests at once were not measured**, so there is no decision for that kind of use.

## Not established

- **GPUs and mixed hardware.** Prima.cpp's scheduler and device profiling exist for that; the layers here were given by hand.
- **Real networks.** netem added delay only: no loss, no jitter, no bandwidth cap. The star's bandwidth ceiling on a home
  uplink (5–6 Mbit per token) is computed from the byte counts, not measured.
- **Models above 8B**, and Prima.cpp's claim about models larger than memory.
- **Nakshatra's and EXO's speed.** Nothing here measures either.
- **The first token of a short prompt through a coordinator**, for the reason above.
- **Whether a worker can be given its layers from its own disk without a patch.** That is the one cheap idea the load
  numbers suggest, and it is unread.

## The published images of this release

`inferhub-coordinator:3.101.0` and `inferhub-node:3.101.0`, both carrying revision `e2ed297`:

- **`verify-lab.sh 3.101.0`: 28 checks of 28 held** — the private group, a member leaving and a drain, the community pick
  and the client's yes, and the relay. The same command on 3.100.0, run before the tag: 28 of 28.
- **`compare-engines.sh` with `INFERHUB_TAG=3.101.0`** (the whole model and the group; the engines beside it do not depend on
  the release): 4 gates of 4. The group gave the whole model's text and kept 94 %, 83 % and 48 % of whole-model speed at 0, 10
  and 30 ms — the same as on 3.100.0 (93 %, 82 %, 47 %), as it should be for a release with no product change.

All three workflows finished green (build-and-test, docker-publish, windows-installer).

## Tests

`tests/InferHub.Tests.Shared`: 457 passed, 0 failed (the phase's test slice; no product code changed).
