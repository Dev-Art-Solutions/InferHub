# A draft model on a group's head — measured before it entered the product (phase 146, 2026-11-23)

Phase 141 measured the engine under a split model and kept it. It listed speculative decoding among the things it could not tell.
This note answers that one question with the same script and the same rule.

## The idea

A split model costs nine to thirteen round trips per token through a star of three workers (133 D3). llama.cpp's speculative
decoding has a small model of the same family *draft* a few tokens, and the big model checks them all in **one** pass. At temperature 0 the
text is the big model's. If the draft runs on the head, its guesses never cross the network, so a group pays its round trips
once per pass instead of once per token.

## How it was measured

`deploy/lab/shards/compare-engines.sh`, `COMPARE_ONLY="whole rpc-star rpc-draft"`, `COMPARE_RTTS="0 30 120"`, three repetitions,
48 tokens, temperature 0. llama.cpp b12182 (CPU, `GGML_RPC=ON`). The target is Llama 3.1 8B Q4 (`87048bcd…`), split over three
workers of 2.5 GiB each. The draft is Llama 3.2 1B Instruct Q4_K_M (same 128 256-token vocabulary), run on the head's CPU
(`-devd none -ngld 0`). Four CPUs per container. `rpc-draft` is `compose.yaml` plus `compose.draft.yaml`; nothing else differs.

**The variant was chosen before the gate**, by a sweep at 30 ms (`runs/2026-11-23/sweep-30ms.jsonl`, two repetitions per prompt):

| Tokens a pass / confidence floor | Short prompt | Long prompt | Drafts accepted |
|---|---:|---:|---:|
| none | 2.44, 3.24 | 2.45, 2.43 | — |
| 8 / 0 | 2.05, 2.38 | 1.97, 2.07 | 38–43 % |
| 4 / 0.75 | 2.18, 2.87 | 2.21, 2.46 | 100 % (few drafted) |
| 3 / 0 | 2.38, 3.12 | 2.24, 2.45 | 54–76 % |
| 6 / 0.6 | 2.36, 2.84 | 2.43, 2.71 | 83–87 % |

6 / 0.6 went to the gate.

## The gate

Decode tok/s, median of three (full table in `runs/2026-11-23/summary.md`):

| Round trip | Star | Star + draft | Short | Long |
|---|---:|---:|---:|---:|
| 0 ms | 6.64 / 6.26 | 7.24 / 6.49 | 1.09× | 1.04× |
| 30 ms | 2.96 / 2.48 | 3.06 / 2.77 | 1.03× | 1.12× |
| 120 ms | 0.62 / 0.62 | 0.89 / 0.86 | **1.44×** | **1.39×** |

Every split answered its whole model's text on every repetition. `report.mjs`: **`rpc-draft: adopt`**. 141's rule is ≥ 1.25× on every prompt at
some shaped round trip, and this passed at 120 ms.

**What it costs:** the long prompt's first token comes 1.8–3.3 s later, because the draft reads the prompt too. Each token costs
~30 % more bytes (0.90 against 0.69 MB), because every pass carries the activations of all drafted tokens, accepted or not. The draft
takes 0.75 GiB more on the head. Load time is the same (62 s against 59 s).

**Why only far away:** on these CPUs, a pass that checks five tokens costs nearly as much as two passes that check one each. The draft
also uses the head's cores between passes. A saving in round trips pays off only once round trips dominate. At 120 ms they do: the star
keeps 9 % of the whole model's speed, the star with the draft 13 %.

## Through the product

The same gate through an InferHub group built from source (`compare-engines.sh`, `COMPARE_ONLY="whole inferhub-group
inferhub-group-draft"`, `COMPARE_HUB_IMAGE=inferhub-coordinator:dev COMPARE_NODE_IMAGE=inferhub-lab-node:dev`, `COMPARE_RTTS="0 120"`).
The node's own state reported *draft llama32-1b.gguf on this node's CPU, up to 6 tokens a pass*.

| Round trip | Group | Group + `Draft` | Short | Long |
|---|---:|---:|---:|---:|
| 0 ms | 6.18 / 6.08 | 6.60 / 7.25 | 1.07× | 1.19× |
| 120 ms | 1.01 / 0.98 | 1.34 / 1.71 | **1.33×** | **1.74×** |

The text was the whole model's on every repetition. The 120 ms runs with the draft spread from 1.18 to 1.72 tok/s. The gain is
larger than for the bare star. One difference is that a group's head holds layers on its own worker (phase 136), so a pass has one
fewer remote hop; **whether that is why is not established**. Run files: `runs/2026-11-23-group/`.

## The decision

**Adopt, opt-in:** `ShardGroups:<name>:Draft` names a second file in the head's models directory (phase 146 D3). It is off by
default: on a local network it gains little (1.03–1.19×) and still costs what is listed above.

## What this cannot tell you

- **GPUs.** A card checks five tokens for nearly the price of one, so the gain could be larger, or the draft on the head's CPU
  could become the bottleneck. Nothing here measured either.
- **A real network.** netem added delay only: no loss, no jitter, and no bandwidth cap, so the 30 % more bytes cost nothing here.
- **Other models.** Only the 8B with this 1B was measured. A 70B needs a draft of its own family, and its acceptance rate is unmeasured.
- **Several requests at once** (phase 147). A draft per slot is llama.cpp's behaviour, and it was not measured.
