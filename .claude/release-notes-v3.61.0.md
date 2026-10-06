# InferHub v3.61.0 — a llama.cpp engine is the whole of llama.cpp

v3.60 could launch `llama-server`, but only as one process per GGUF: one model, fixed at launch, chat or
embeddings, and nothing the hub could pull, load or unload. Everything else llama.cpp does — reranking,
`/infill` for editors, `/tokenize`, GBNF grammars, its samplers — was out of reach of a client.

v3.61 runs llama.cpp's **router**: one `llama-server` over a directory of GGUFs and a set of presets,
loading each model when it is first asked for. The hub pulls models into it from Hugging Face, warms,
unloads and deletes them, and llama.cpp's own routes reach clients through the fleet.

## What's new

### A router instead of a process per model

```jsonc
"gguf": { "Type": "llamacpp",
          "Serve": { "Executable": "/opt/llama/llama-server", "ModelsDir": "/models", "MaxLoaded": 2,
                     "Presets": {
                       "nomic":       { "Model": "/models/nomic-embed-text.gguf", "Embeddings": true },
                       "jina-rerank": { "HfRepo": "gpustack/jina-reranker-v1-tiny-en-GGUF:Q8_0", "Reranking": true } } } }
```

- Every `.gguf` in `ModelsDir` is a model by file name; a preset names a model and says what it is for
  (`Embeddings`, `Reranking`), with any llama.cpp option under `Settings`. `MaxLoaded` caps what stays in
  memory (least recently used goes).
- `Serve:Model` is still one GGUF exactly as in v3.60. An already-running router is `BaseUrl` + `Router: true`.

### Models managed from the hub

- `POST /api/admin/nodes/{id}/models/{owner%2Frepo:quant}/pull?engine=gguf` — llama.cpp's own Hugging
  Face download. `hf.co/owner/repo:quant`, Ollama's spelling, works too.
- `…/warm`, **`…/unload`** (a new command — Ollama answers it with `keep_alive: 0`), `DELETE …`
  (downloaded repos only; a file in `ModelsDir` is refused).
- `?engine=` names the engine. Without it, a model an engine reports goes there; a pull on a node with
  two engines that can pull is refused naming both.
- The console's model panel has an engine field and an **Unload** button.

### llama.cpp's routes, through the fleet

- `POST /v1/llamacpp/{completion|infill|tokenize|detokenize|apply-template|embedding}`,
  `GET /v1/llamacpp/props?model=` — on the hub and on a solo node. The body goes through untouched; the
  engine's errors keep their sentence. No streaming, no `/slots`, `/metrics` or `POST /lora-adapters`.
  `completion` and `infill` are billed in tokens (kind `llamacpp`).
- `POST /v1/rerank` (Jina/Cohere shape), answered by a llama.cpp reranker or the cross-encoder tool.
- Ollama's `top_k`, `min_p`, `typical_p`, `repeat_penalty`, `repeat_last_n`, `mirostat*`, `num_keep` now
  reach a llama.cpp engine under llama.cpp's names.

### Fixed on the way

- **A `Backend:Engines` node never offered model management** (since v3.60): it told the hub at
  registration, before its engines had started, that it could not manage models. Now declared from
  configuration.
- **A pulled model was not routable until the next model refresh (60 s by default), and a deleted one
  stayed routable as long** (since phase 26's model commands). The node now reports its models as soon as a
  pull or delete finishes.

## Verified against real engines

Windows 11, llama.cpp b11417 (win-cpu), Ollama 0.34.4, a real coordinator and a real meshed node built
from this commit, one node running `ollama` + `gguf` (router: `ModelsDir` with `qwen2.5-0.5b.gguf`, preset
`nomic` embeddings, preset `jina-rerank` from Hugging Face, `MaxLoaded: 2`).

- The node launched one `llama-server --models-dir … --models-preset <node-written ini> --models-max 2`;
  the hub showed engine `gguf` running with kinds `chat, embed, rerank, llamacpp` and models
  `jina-rerank, nomic, qwen2.5-0.5b`.
- `/api/chat` on `qwen2.5-0.5b` with `top_k/min_p/repeat_penalty/num_predict` answered; `/v1/embeddings`
  on `nomic` answered.
- `/v1/rerank` on `jina-rerank`: the first call downloaded the reranker from Hugging Face and answered in
  6.8 s, the cat sentence above the stock-market one.
- `/v1/llamacpp/tokenize` → `[14990,1879]`; `completion` with a `yes|no` grammar answered `" no"`
  (7 + 2 tokens); `infill`, `apply-template`, `props` (n_ctx 32768) answered; `slots` → 404.
- Pull without `?engine=` → refused: "2 engines on this node can pull … (gguf, ollama); name one with
  ?engine=<name>". With `?engine=gguf`, `bartowski/SmolLM2-135M-Instruct-GGUF:Q4_K_M` was downloaded and
  **routable 14 s after the pull started**; a chat through the hub answered.
- Warm `qwen2.5-0.5b` → `loaded`; unload → `unloaded`; delete of it → refused ("comes from
  Serve:ModelsDir"); delete of the pulled repo → gone from the router and from the hub's routing.
- Killing the node (`Stop-Process -Force`) left no `llama-server` of ours behind — the router's per-model
  children are in the node's Job Object too.
- No prompt, query or document text appeared in the coordinator's or the node's log.

**Found by the live run, fixed before this release:** the management flag above; the refresh delay
above; and a preset's `HfRepo` appearing a second time under its repo name (llama.cpp lists its download
cache), where it would have been declared a chat model — such duplicates are no longer listed.

## Not established — said out loud

- **A multimodal model through the router.** No `mmproj` on the test box; the directory convention and
  the `Mmproj` preset key are llama.cpp's own and were not exercised.
- **A Linux router.** Windows only; the published node image does not carry llama.cpp (unchanged from
  v3.60), so there is no image-level check of the router.
- **Pull progress in bytes.** The router reports `downloading` and nothing more; the progress frame says so.
- Ollama's samplers were seen arriving in the request body in tests; their effect on llama.cpp's output
  was not measured.

## Upgrading

Nothing to change. A v3.60 `Backend:Engines` config behaves as before, except that every `llamacpp`
engine also declares the `llamacpp` capability, and a node with an Ollama engine now offers model
management in the console (it always should have).
