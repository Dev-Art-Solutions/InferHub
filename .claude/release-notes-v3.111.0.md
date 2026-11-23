# InferHub v3.111.0: a draft model on the head, for a group far apart

Phase 146, the fifth of the shared-compute track (142–149). Each token of a split model visits every member in turn. Across the
internet the round trip, not the bandwidth, is what a token costs (133 D3). llama.cpp's speculative decoding has a small model of the
same family guess a few tokens, and the big model checks them all in one pass. With the draft on the head, a group pays its round
trips once per pass instead of once per token. Phase 141's rule decided whether this could enter the product, and it passed only at
a long round trip.

```json
"ShardGroups": { "llama-8b-split": { "Head": "shard-a", "Members": ["shard-b", "shard-c"],
                                     "Model": "llama31-8b.gguf", "Draft": "llama32-1b.gguf" } }
```

```sh
LAB_MODEL_DIR=… LAB_DRAFT_DIR=… COMPARE_ONLY="whole rpc-star rpc-draft" COMPARE_RTTS="0 30 120" deploy/lab/shards/compare-engines.sh
```

## What changed

- **`ShardGroups:<name>:Draft`** (default unset, no change): a second file in the head's `Node:Shards:ModelsDir`, with the
  model's vocabulary. The head runs it on its own CPU as llama.cpp's draft model.
- **`DraftTokens`**: how many tokens the draft guesses per pass, 1–32. 0 means the measured 6. The confidence floor is fixed at the
  measured 0.6.
- **The draft is placed on the head's CPU by flag** (`-devd none -ngld 0`). Left to llama.cpp it follows `--device`, and a group's
  head names only its RPC devices, so every drafted token would have crossed the network.
- **A head without the file runs the group without the draft and says so.** `GET /api/admin/shards/groups` shows `draft` on the
  group as reported: *draft llama32-1b.gguf on this node's CPU, up to 6 tokens a pass*, or *no draft: 'x' is not in …; runs without
  one*. A missing draft costs speed, never the answer.
- `ShardGroupSpec.draft/draftTokens` and `ShardGroupState.draft` are additive and absent when unset. A head older than 3.111
  ignores them.
- **The lab:** `compose.draft.yaml`; `rpc-draft` and `inferhub-group-draft` in `compare-engines.sh`; a draft verdict by 141's rule in
  `report.mjs`; `COMPARE_HUB_IMAGE`/`COMPARE_NODE_IMAGE` for images built from source; `tunnel-lab.sh` `LAB_DRAFT`. Found while
  running it: the script wrote docker's error text into `meta.json` for an image that is not there. It now writes one line or `missing`.

Zero new packages; `InferHub.Shared.csproj` still empty.

## What was measured

The 8B (Llama 3.1 Q4, `87048bcd…`) with Llama 3.2 1B Instruct Q4_K_M as the draft, llama.cpp b12182, CPU only, 4 CPUs per
container, one PC. Three repetitions, 48 tokens, temperature 0. Decode tok/s, short / long prompt, median.

**The variant was chosen before the gate**, by a sweep at 30 ms. Drafting 8, 3, or 4 with a 0.75 floor all came out at 0.92–1.05×.
6 with a 0.6 floor was best and had 83–87 % of its drafts accepted.

**The gate: llama.cpp's own RPC star, head plus three workers:**

| Round trip | Without | With the draft | |
|---|---:|---:|---:|
| 0 ms | 6.64 / 6.26 | 7.24 / 6.49 | 1.09× / 1.04× |
| 30 ms | 2.96 / 2.48 | 3.06 / 2.77 | 1.03× / 1.12× |
| 120 ms | 0.62 / 0.62 | 0.89 / 0.86 | **1.44× / 1.39×** |

**`rpc-draft: adopt`**: past 1.25× on both prompts at 120 ms, with the whole model's text on every repetition.

**Through the product: an InferHub group built from source, three nodes, through the coordinator:**

| Round trip | Without | With `Draft` | |
|---|---:|---:|---:|
| 0 ms | 6.18 / 6.08 | 6.60 / 7.25 | 1.07× / 1.19× |
| 120 ms | 1.01 / 0.98 | 1.34 / 1.71 | **1.33× / 1.74×** |

The node reported the draft running, and every repetition gave the whole model's text. The draft's 120 ms runs spread from 1.18 to
1.72 tok/s.

**What it costs:**
- 0.75 GiB more memory on the head.
- ~30 % more bytes per token (0.90 against 0.69 MB): a pass carries every drafted token, accepted or not.
- 1.8–3.3 s more before the first token of a long prompt, because the draft reads the prompt too.

**Why it pays only far away:** on these CPUs, a pass that checks five tokens costs nearly as much as two passes, and the draft takes
the head's cores between passes.

Method, sweep and run files: `docs/research/draft-model-on-the-head.md`, `docs/research/runs/2026-11-23*`.

## The published images

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.111.0` and `inferhub-node:3.111.0` (both revision `a0d1fa2`, pulled from GHCR)
were run through the same comparison: `compare-engines.sh`, `INFERHUB_TAG=3.111.0 COMPARE_REVISION=a0d1fa2
COMPARE_ONLY="whole inferhub-group inferhub-group-draft" COMPARE_RTTS="0 120"`.

- **All four gates PASS.** The report confirms the images were built from `a0d1fa2`.
- **The draft against the same group, short / long prompt:** 1.04× / 1.18× at 0 ms, and **1.33× / 1.74× at 120 ms**. The long
  prompt went from 0.98 to 1.71 tok/s, and the text was the whole model's on every repetition.
- **The node reported the draft running:** *draft llama32-1b.gguf on this node's CPU, up to 6 tokens a pass*.
- **A head without the file**, tested with `LAB_DRAFT=not-here.gguf` on the same images: the group reached `running` and answered,
  and `draft` said *no draft: 'not-here.gguf' is not in /models; runs without one*.

Run files: `docs/research/runs/2026-11-23-published/`.

## What is not established

- **GPUs.** A card checks several tokens for nearly the price of one. The gain could be larger, or the draft on the head's CPU could
  become the bottleneck. Nothing here ran on a card.
- **A real network.** netem added delay only: no loss, no jitter, no bandwidth cap. On a capped home uplink, the 30 % more bytes
  could eat into the gain.
- **Other models.** Only the 8B with this 1B was measured. A 70B needs a draft of its own vocabulary family, and its acceptance rate
  is unmeasured.
- **Why the group gains more than the bare star.** A group's head holds layers itself; whether that is the reason was not measured.
- **Several requests at once** (phase 147).
- **Community and auto groups** pass the setting through unchanged. Only a private group was run. An auto group's head is still
  picked by the model file alone.
