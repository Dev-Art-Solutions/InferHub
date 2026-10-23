# InferHub v3.75.0 — engines that know their limits

Phase 111. v3.67 holds every runtime on a node to its limits from outside — a Job Object or a cgroup, affinity, a
VRAM watchdog. Two runtimes size themselves from what they see, and neither sees any of that:

- **colibri 2.0**, with no limit told, planned **147.5 GB** of expert cache ("88% of what the OS still offers") on a
  runtime limited to 8 GB, and would have met the limit as a failed allocation.
- **Strata** filled the card down to its reserve and used **14.9–21.9 GB of a 12 GB limit**: the watchdog stopped
  it, its supervisor started it, it was stopped again — 102 D5's loop, by design, every start.

So the node now **tells** them, in each engine's own words, when it starts them — and holds them exactly as before.

| Limit | colibri is told | Strata is told |
|---|---|---|
| `MemoryMiB` | `--ram` (90%, whole GB; the launcher maps it per family, GLM-5.3 → `GLM53_EXPERT_GB` = G − 8) | `--resident-budget-gib` lowered to limit − 8 (only a model that has a RAM budget) |
| `Cores` / `CpuPercent` | `OMP_NUM_THREADS` | `"pool_cores": "1,…,N−1"` |
| `VramMiB` | `COLI_VK_DEVICE_CAP_MB`, `CUDA_EXPERT_GB` | `--vram-reserve-mib` raised by (card − limit), the card's size from NVML |
| `Gpus` | `COLI_VK_DEV`; none → `COLI_CUDA=0`, `COLI_VULKAN=0` | none → not started, with the sentence |

- **Never `--gpu`/`--vram` to colibri**: those flags make `coli` exit on a build without CUDA (every Linux archive and
  the plain Windows one). The variables reach the same switches and are ignored where they do not apply.
- **Strata starts on a copy of its run config** (`.inferhub-limits-<file>` beside it): its engine reads options from
  the config, not from the server's command line. The original is never written; with nothing to tell, it starts.
- **A limit that changes what a runtime was told restarts it**, as a GPU-list change always has. Not a breach.
- **`told`** is a new field on every runtime's row in the limits report — `/api/status`, the console's *Runtime
  limits* panel, the node's `/api/admin/engine-limits` — the lines the runtime was given at its last start.

## colibri 2.0 and System One

The `:colibri` image now carries **colibri v2.0.0** (from 1.12.1, sha256-pinned). 2.0's engines are Vulkan builds; the
image has no Vulkan loader, so they run on the CPU there exactly as 1.12.1's did. Every text engine takes up to 16 KV
slots now (1.12 allowed more than one only on GLM-5.2/5.3).

**colibri 2.0 removed `/v1/brio`.** Its one decision API is System One (`POST /v1/systemone`, the shape of TypeSafe's
Jev: a state, typed questions `noul`/`choice`/`score`, a probability per option). v3.75 serves it on the hub and on a
solo colibri node, as a `ToolJob` under a **new capability, `decide`** — a separate kind so a hub never sends a System
One question to a 3.74 node, which would post it to `/v1/brio`. The body passes through untouched, billed as
`input_tokens + output_tokens` read, kind `decide`. `model` is required at the edge, because the hub routes by it.
`/v1/brio` stays for 1.12 engines; against a 2.0 engine it answers a 404 that says to use `/v1/systemone`.

## Strata 0.2

Strata moved from 0.1.41 to the 0.2 line (the app, the Terminal, engine packages from a signed feed); the live run used
**0.2.3 with engine 0.1.48**. The node drives it as before — `serve/server.py --config`, `setup.py --setup --yes
--no-start` — and both kept their flags. The sizes the hub can install grew from 9 to **21**, from 0.2.2's `setup.py`
(unchanged in 0.2.3): Swift IQ3_S; Unsloth UD-Q5_K_XL, UD-Q6_K_XL, Q8_0; Gyro-S, Gyro-M; NVFP4, NVFP4-NVIDIA, NVFP4-QAD
(one repo each); and **Qwen3.6-35B-A3B** in three sizes, Strata's first model that is not Qwen3.8-Flash-Next.

## Found by running it

A hub and a node from source on this box (Threadripper, 64 logical, 256 GB, 3090 Ti 24 GB; models on a 4 TB SMR hard
disk), colibri 2.0's Windows archive and Strata 0.2.3 from source.

**colibri, OLMoE int8.** No limit: `147.5 GB budget (88% of what the OS still offers)`. Node limit 8 GiB / 8 cores:
told `--ram 7`, `OMP_NUM_THREADS=8`; `7.0 GB budget (RAM_GB)`, used 7.3 GB, "Paris". The hub narrowed it to 4 GiB /
4 cores (16 GiB was a 409 naming the node's 8192): the runtime was restarted told `--ram 3`, `3.0 GB budget`, used
3.5 GB, "Jupiter". `/v1/systemone` through the hub: `technical` 0.9999, urgency 0.947, 84 + 5 tokens; `/v1/brio`
against 2.0: the 404 sentence; `jev-latest`: the model 404.

**Strata Coder IQ1_M under `vramMiB: 12000, cores: 8`.** Two findings, both fixed before the release:

1. **Strata 0.2's `user_args` are applied over `args`**, the person's value winning for the same flag. A told
   reserve of 13264 in `args` lost to a `--vram-reserve-mib 964` in `user_args`, and the watchdog stopped Strata at
   14943 MiB. The teller now writes a told flag into both.
2. **Strata 0.2's server gives its token an owner-only default DACL** (`strata_control/private_files.
   adopt_user_defaults`), and **a process with one cannot start a child inside a Job Object that has an affinity
   limit** — `CreateProcess` answers ERROR_ACCESS_DENIED, every time. Reproduced in fifteen lines of Python with no
   node: the DACL alone, or the affinity job alone, both start the child; together, never. Strata's core limit is now
   each process's own affinity (`SetProcessAffinityMask`, inherited by children, re-applied each poll as Linux's is),
   reported `os (process affinity, re-applied each poll)`. Every other runtime keeps the job's affinity.

After both: told `pool_cores 1-7`, `--vram-reserve-mib 13528 (card 24564 MiB, 12000 MiB allowed)`; Strata answered
(`s[::-1]`, 61 s with the cold load) at **11697 MiB**, no stop.

Also found and fixed: the Job Object tried to switch off a CPU rate it had never set, and Windows refused with error
87 on every limit without a CPU share — logged, harmless, 102's.

**GLM-5.3-Flash (321B), converted to E: for reuse.** `convert_glm53.py` from colibri 2.0, 62/62 shards, **194.7 GB**
(the number colibri's own docs give). Served by the node with `colibri: {memoryMiB: 98304, cores: 32}`: told
`--ram 86`, `OMP_NUM_THREADS=32`; the engine held **57.6 GB of 96 GiB**, no breach.

| request | answer | time |
|---|---|---|
| cold, streamed (195 GB off a hard disk) | "Paris" | 6 min 16 s |
| warm, streamed | "Red, blue, yellow" | 25 s |
| warm, not streamed | "56" | 89 s |

A non-streamed request sent right after the cold load ran past **300 s** and was cancelled: the node's
`Upstream:TimeoutSeconds` (300) ended its call to colibri. A stream is not cut that way. For a model this size on a disk,
raise the node's `Upstream:TimeoutSeconds` and the hub's `Dispatcher:Deadlines:chat` — said in the README.

Strata's own update to engine 0.1.47/0.1.48 failed three times on 0.2.2 ("did not answer a one-token probe") — the
Windows test-start bug 0.2.3 fixed (#3683); with 0.2.3 it installed on the first run. On this disk Strata itself asks for
`--ple-io mmap` (random 4 KiB reads at ~1 MB/s); that was set in the Coder's `user_args` by hand, outside InferHub.

## What changes with no config changed

Nothing a node without limits can see: with no limit on colibri or Strata, no teller says anything, and both start
with v3.74's command lines and environment. `told` is absent from the report. `decide` is a new capability colibri
nodes declare beside `chat` and `score`; a hub from before this release ignores it.

## Not established

- **A GPU colibri.** 2.0's Vulkan tier was not run: the box's card was held by another project's Ollama most of the
  day. `COLI_VK_DEVICE_CAP_MB`, `COLI_VK_DEV` and `CUDA_EXPERT_GB` are passed as colibri's docs describe them; that
  colibri stays under a VRAM limit with them is not measured.
- **Two GPUs.** `COLI_VK_DEV2` and a Strata layer split over two cards: one card here.
- **Strata on Linux under a core limit**: the DACL interaction is a Windows one; Linux affinity was already per thread.
- **The Strata families added from 0.2.2's table** were not installed through the hub; the map was checked against
  `setup.py`, and Strata's own parser decides.
- **`/v1/systemone` against a decision model** (Laya, GLiNER2.5-Decide, Clef): OLMoE answered through its logprob
  channel only.
