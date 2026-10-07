# InferHub v3.63.0 — a Hugging Face link from the hub, downloaded once by the node, served by llama.cpp or colibri

Until now a node held the models somebody had put on it. A llama.cpp router could pull into llama.cpp's
own cache (v3.61), and a colibri model was made by hand with `coli convert`. v3.63 lets the coordinator
hand a node a Hugging Face link: the node fetches the model itself, keeps it, and the engine that can
serve it does — from then on, across restarts.

**One thing first: colibri does not read GGUF.** It has its own format, made by `coli convert` from a
safetensors Mixture-of-Experts checkpoint of a family it knows. llama.cpp needs GGUF. So one download
never feeds both engines: a GGUF goes to llama.cpp, a checkpoint is converted for colibri. For the same
model on both, pull its GGUF repo and its checkpoint.

## What's new

```
POST /api/admin/nodes/{id}/huggingface   {"url": "https://huggingface.co/bartowski/SmolLM2-135M-Instruct-GGUF", "quant": "Q4_K_M"}
POST /api/admin/nodes/{id}/huggingface   {"url": "https://huggingface.co/allenai/OLMoE-1B-7B-0924-Instruct"}
```

The console's **Model management** has a Hugging Face link field, a quant field and **Download to node**;
progress is in the command feed. On the node, `HuggingFace:Enabled=true` turns it on — off by default,
because a GPU box reaches the internet only when its operator says so.

### A GGUF goes to the llama.cpp router

- Downloaded into the router's `Serve:ModelsDir` as its own directory (`<dir>/SmolLM2-135M-Instruct-Q4_K_M/`)
  — a sub-directory is one model in llama.cpp's convention, so split files and an `mmproj` travel together.
- Pinned to the commit the branch pointed at, resumed with `Range` after an interruption, size and sha256
  checked against Hugging Face (a mismatch is deleted), renamed only when every file is whole.
- `quant` picks the file; a repo with several models and no quant is refused with the list; a link to one
  `.gguf` takes that file.
- The router reads its directory only when it starts, so the node restarts that engine (in-flight requests
  drain first).

### A checkpoint is converted for colibri

- No `.gguf`, and `config.json` beside `*.safetensors`: the node runs `coli convert --repo … --model …`.
  colibri picks its family's converter, downloads the shards itself and deletes each after converting it.
  One conversion at a time. The result appears in the colibri catalogue under the repo's name, lowercased.
- The `:colibri` image now carries colibri's converter environment (`/opt/colibri/mio_env`: CPU torch
  2.9.1, numpy, safetensors, huggingface_hub — pinned, imports checked at build time). That is why the image
  is larger.

### Once, and only what it made

- A model the node downloaded carries `.inferhub-source.json` (repo, commit, files). A second pull of the
  same thing is "already downloaded"; the model is there after a restart.
- `DELETE /api/admin/nodes/{id}/models/{name}?engine=huggingface` removes only a marked directory — never
  one the operator put there — with the router stopped around it.
- `HuggingFace__Token` for gated and private repos; `HuggingFace:LlamaCppEngine` when several routers have a
  `ModelsDir`; `HuggingFace:Convert=false` keeps checkpoints out. A link to any other host, `..` or a
  backslash in a path is refused by the hub before it travels.

## Verified against the real Hugging Face

A coordinator built from this commit and a node image built from it (`Dockerfile.colibri`), on a fresh
Docker volume, with two engines: a llama.cpp b11417 router (`Serve:ModelsDir=/models/gguf`) and a colibri
catalogue (`/models/colibri`), `HuggingFace:Enabled=true`.

- `bartowski/SmolLM2-135M-Instruct-GGUF` + `Q4_K_M` from the hub: one file, 105 MB, downloaded and
  verified in about 6 s; the router restarted and the hub routed the model 9 s after the request; a chat
  through the hub answered in 0.7 s.
- `allenai/OLMoE-1B-7B-0924-Instruct` from the hub: `coli convert` chose `convert_olmoe_merged.py`, downloaded
  the three shards and converted them in about 4 minutes into a 7 GB colibri model; a chat through the hub
  answered "Paris. The capital of France is Paris" in 9.8 s.
- The node restarted with the same volume: both models were there, nothing downloaded again. A second pull
  of the GGUF downloaded nothing and did not restart the router.
- A link to another host: 400 at the hub.
- No Hugging Face or CDN URL in the node's log.

**Found by the live run, fixed before this release:**

- **`coli convert` picked GLM-5.2's converter for an OLMoE and refused it.** The node ran `coli` under the
  image's own `python3`, which has no `huggingface_hub`, so `coli` could not read the checkpoint's family.
  The node now runs it under colibri's converter environment, as `coli` itself prefers.
- **The catalogue listed a model while it was still being converted** — the converter writes `config.json`
  early, and the hub could have sent requests to half a model. Conversion now happens in a hidden
  directory, renamed to the model's name only when it is complete.
- **On a fresh volume the router's directory did not exist**, so the llama.cpp engine sat `failed` until the
  first download. The node now creates the target directories when it starts.
- **The HTTP client logged every request**, including the CDN redirects that carry a signature. It no
  longer does; the node logs what it downloaded.

## Not established — said out loud

- **A gated repo with a real token**: the 401 sentence and the `Authorization` header are tested against a
  Hub-shaped socket, not a real gated model.
- **Split GGUF files and an `mmproj`** through the real router: tested on the selection and the directory
  layout, not loaded by llama.cpp.
- **Families other than OLMoE** through `coli convert`. Their converters are colibri's; the larger ones need
  far more disk and RAM, and hours.
- **Resuming a real interrupted download** from huggingface.co (resume is tested against the socket).
- A safetensors model for **llama.cpp** (GGUF conversion) is not offered — link the model's GGUF repo.

## The published-image check

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.63.0` and `inferhub-node:3.63.0-colibri`, both with
`org.opencontainers.image.revision` = `8785cfd` (the tag), on a Docker network with keys on, a fresh volume,
the same two engines. Without the admin key the `/huggingface` route answered 401. The GGUF and the OLMoE
checkpoint were requested together: the GGUF was routable 19 s later; the conversion staged in
`.converting-olmoe-1b-7b-0924-instruct` (the catalogue did not list it) and appeared as
`olmoe-1b-7b-0924-instruct` when complete, about 3.5 minutes later. Then both answered through the hub
("The capital of France is Paris." in 0.1 s; "Paris. The capital of France is Paris" in 11.3 s). No Hugging
Face URL and no prompt in either container's log.

**It also found a bug, fixed in v3.63.1:** a chat sent while the conversion ran was not answered — the
node's model-command handler held its connection for the whole command. See `release-notes-v3.63.1.md`.

## Upgrading

Nothing changes unless `HuggingFace:Enabled=true`. An engine named `huggingface` under `Backend:Engines`
is now refused at startup (the name is reserved for these downloads). The `:colibri` image is larger by
the converter environment.
