# InferHub v3.79.0 — will it fit? Choose a context, get the engine

Phase 118, plus InferHub Desktop 0.4.0 on its own tag. A node already downloads a model from a link (3.63) and
installs Strata's sizes (3.64), but nothing said whether the model would run, or what context it would get. A
111 GB GGUF on a 24 GB card downloaded for an hour and then either crawled or did not load. v3.79 answers first:
you paste a link and say how much context you need, and the node says which of its engines can run the model,
where the model would sit, and why, with the numbers. It then installs the model on the engine you pick, with that
context.

```http
POST /api/control/fit          {"link": "unsloth/Qwen3-Next-80B-A3B-Instruct-GGUF", "context": 32768}
POST /api/control/fit/install  {"link": "…", "engine": "llamacpp", "variant": "Q5_K_S", "context": 32768}
```

## What it does

- **The link.** It can be a Hugging Face repo or file, or a model in Ollama's library (`qwen2.5:7b`,
  `ollama.com/library/…`). The node reads the model's GGUF header with a Range request: a few megabytes from
  Hugging Face or from Ollama's registry, never the weights. For a checkpoint it reads `config.json`.
- **There is no minimum context.** You choose it, and it decides the engine. The same 80B mixture-of-experts sits
  with its experts in RAM at 32K, and is split across the bus at 256K on the same card.
- **The arithmetic is shown.** The estimate is weights + the KV cache for your context + a working reserve,
  against the NVIDIA cards, the RAM and the free disk where that engine keeps models. Each figure is lowered by
  your limit for the runtime. The KV cache comes from the model's own layers and heads:
  - Hybrid models are counted from their tensors. Qwen3-Next keeps a cache in 12 of its 48 layers, and its keys
    do not say so.
  - MLA models (DeepSeek) are counted by their latent.
  - Ollama gives each parallel slot a whole context, so its cache is counted once per slot.
- **An engine is offered only if it can load the model.** Ollama and llama.cpp take a GGUF; Ollama does not take
  one split into several files. colibri takes a mixture-of-experts checkpoint, and Strata only its own repos.
  The ranking is:
  - Where the model sits comes first: all on the card, then experts in RAM (llama.cpp), Strata, split, RAM, CPU,
    and last read from disk.
  - Then quality, up to about six bits a weight.
  - Quants under three bits rank lower, and under two lower still.
- **Strata and colibri are asked, not second-guessed.** They plan their own GPU placement, so for them the node
  runs their own planners:
  - **Strata:** its size table and recommendation for this PC (`tools/strata_mcp.py`, the rules its setup uses).
    RAM and VRAM are lowered to your limits first.
  - **colibri:** `coli plan --json` for a model already on the node. It gives the dense layers and hot experts on
    the card, the warm experts in RAM, and the cold ones on disk.
- **colibri on the GPU.** `Colibri:Serve:Vulkan=true` sets `COLI_VULKAN=1` for the engines the node launches.
  colibri's own Vulkan placement then puts the dense layers and a hot-expert tier on the card. A CUDA build of
  colibri uses the card by itself.
- **The context goes with the install, to where each engine keeps it:**
  - Strata: `setup.py --context`.
  - llama.cpp: `ctx-size` in the router's preset. A new context for a model already here restarts the router.
  - colibri: `coli serve --ctx` at every launch.
  - Ollama: `num_ctx` on every request that sets none (`<DataDirectory>/model-contexts.json`). The caller's own
    value wins.
  - The new `ModelCommand.Context` carries it. It is appended and nullable, and a 3.78 node ignores it.
- An option the estimate says does not fit is refused unless you send `"force": true`.

## Also fixed (found live)

**Unloading a catalogue model that a request had loaded stopped nothing.** `POST
/api/control/catalog/{colibri|strata}/{model}/unload` only took the model out of the pins, and such a model was
never pinned: GLM-5.3 kept 23 GB of the card after an "unload". The route now also stops it (live: Strata's card
back to 938 MiB, its processes gone). The hub's profile path is unchanged.

## InferHub Desktop 0.4.0

**Check fit…** beside Pull on the Models tab. The dialog asks for the context first (typed as `32k` or `128K`, or
a quick pick up to the model's trained context), then shows:

- one line for the model and one for the machine;
- one row per engine, with its best size, the other sizes that fit, and the node's reasons;
- an **Install** button.

## Measured on a GPU (RTX 3090 Ti, 256 GB RAM; EvoMesh stopped to free the card)

| Engine, model, context | Estimate | What it took |
|---|---|---|
| Ollama, llama3.1 8B, 8K | 6 310 MiB, card | 5 412 (nvidia-smi +5 677) |
| Ollama, llama3.1 8B, 64K | 14 052, card | 13 036 (+13 304) |
| Ollama, mistral 7B, 32K | 9 106, card | 8 224 (+8 504) |
| Ollama, gpt-oss 20B, 32K | 15 530, card | 12 307 (+13 371) — its sliding-window layers are counted at full context |
| llama.cpp (Vulkan), llama3.1 8B, 32K | 9 381, card | +8 400 |
| llama.cpp, a 35B-A3B MoE (qwen35moe), 64K | 23 283 of 23 336, card | +21 603 |
| llama.cpp, a 70B Q4_0, 4K | split, 60 % on the card | 47 of 80 layers on the card, +22 270 |
| colibri 2.0 + Vulkan, GLM-5.3-Flash 321B, 32K | plan: 21 074 on the card, 177 GiB RAM | card +22 115; warm "Paris" in **8–9 s** (25 s on the CPU in 3.75) |
| Strata 0.2.3, Coder IQ1_M, 32K | Strata's own verdict "fits"; 23 028 card, 23 270 RAM | card +21 834, 26 GB working set; 48 tokens warm in 6 s |

llama.cpp's own `llama-fit-params` agreed with the estimate's placement in all three llama.cpp cases. The
estimate is an upper bound, usually 0.6–1 GB over. colibri's plan was under by about 1 GB, because its tier fills
the card down to its 1 GiB reserve. Options from a plan now count the card that way.

## Upgrading

Nothing changes until `/fit` is called. A pull without a context is the pull it was. `Colibri:Serve:Vulkan` unset
leaves colibri's environment as before.

## Not established

- **AMD and Apple GPUs.** The node reads NVIDIA cards only, so such a box is estimated as the CPU, and the reply
  says so.
- **Ollama running Qwen3-Next.** It was not tried. Ollama is offered only that model's 1-bit size, because the
  bigger quants are split files.
- **colibri's CUDA tier.** The CUDA build here has no GLM-5.3 engine.
- **A context for a colibri model converted by hand.** Such a model has no marker to keep the context in.
- **KV quantization** (`q8_0` halves the cache) is not counted.
- **Sliding-window layers** are counted at the full context, so gpt-oss comes out about 2 GB over.
- **A hub cannot ask yet.** `/fit` is the node's own; phase 120 brings it to the coordinator, switched on at the
  node's install.

## Verification

`dotnet test InferHub.sln`: **2 514 passed, 63 skipped** (Desktop 29, Shared 410, Coordinator 896, Node 655, Mesh 524). The new tests read a GGUF the writer builds in llama.cpp's layout, a fake Hugging Face and a fake Ollama registry over real HTTP, and canned answers copied from `coli plan` and Strata's planner on the box above; removing the hybrid-layer guard or the experts-in-RAM placement turns them red.

## The published release, checked

`inferhub-node:3.79.0` was pulled from GHCR (revision label `aa268ca`, the tag's commit). Its `app/` was extracted and
run natively on the same Windows box, with the same four engines: Ollama, a Vulkan llama.cpp router, colibri 2.0
with `Colibri:Serve:Vulkan=true`, and Strata at `E:/strata`. These were the published binaries, not a container, so
the image's OS layer was not tested.

| Call | Result |
|---|---|
| `/fit zai-org/GLM-5.3-Flash` @32K | colibri's own plan: 21 620 MiB on the card (the card less its reserve), 186 GiB in RAM, nothing on disk |
| `/fit` Qwen3.8-Flash-Next @128K | Strata's own verdicts. IQ3_S comes first, as Strata recommends for this PC |
| `/fit` Qwen3-Next-80B GGUF @32K | hybrid 12 of 48 layers; llama.cpp with experts in RAM, 4.3 GiB on the card |
| `/fit llama3.1:latest` @64K | Ollama, all on the card, 14 052 MiB |
| `/fit/install` Qwen2.5-0.5B GGUF, llama.cpp, context 12288 | downloaded; the router's `n_ctx` is 12288 |
| Strata Coder IQ1_M, chat, then catalogue unload | card +19.7 GB, answered; card back to 1.6 GB after unload |

InferHub Desktop 0.4.0 (`InferHub-Desktop-0.4.0-windows-x64.zip`) was downloaded from its release. Its checksum
matches `SHA256SUMS.txt`. Its Core is 0.4.0 and its node 3.79.0, both built from `aa268ca`, and it carries the
`/fit/install` client. The packaged app's window was not opened; the dialog was rendered from the source build of
the same commit (`.claude/verification-v3.79.0-desktop.png`).
