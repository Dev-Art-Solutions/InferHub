# InferHub v3.64.0 — Strata: a 125B model on one gaming GPU, its sizes picked and installed from the hub

[Strata](https://github.com/Niko1221/Strata) (MIT) runs **Qwen3.8-Flash-Next** — 125 billion parameters,
24,576 experts — on one 12 GB+ graphics card plus 32–64 GB of RAM: the busiest experts on the card, all of
them in RAM, a lookup table on the SSD. It is one model in nine sizes over four versions (the original,
Swift 1.5, the Coder, Unsloth's 4-bit), each prepared for the box by Strata's own installer.

v3.64 makes a node serve a Strata install the way v3.62 made it serve a colibri catalogue: **every
installed size is a model the fleet can route to**, the one a request names is started, the coordinator
picks which stays loaded — and the coordinator can **install another size from Hugging Face**.

## What's new

```jsonc
"Backend":     { "Type": "strata" },          // or a Backend:Engines entry of Type "strata"
"Strata":      { "Root": "/opt/strata" },      // a Strata checkout set up with its own installer
"HuggingFace": { "Enabled": true }             // only if the hub may install more sizes
```

```
POST /api/admin/nodes/{id}/strata/install                 {"model": "strata-q2_0"}
POST /api/admin/nodes/{id}/strata/models/{model}/load     # pin it; on the one-slot default, a switch
POST /api/admin/nodes/{id}/strata/models/{model}/unload
POST /api/admin/nodes/{id}/strata/on-demand/enable|disable
POST /api/admin/nodes/{id}/huggingface                    {"url": "https://huggingface.co/ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF", "quant": "IQ2_XS"}
```

- **Every installed size is a model** — `strata-iq2_xs`, `strata-coder-iq1_m`, … one per `strata-*.json`
  config Strata's setup wrote, named by its file, listed whether loaded or not. Chat only.
- **A request starts its model**: the node runs Strata's own `serve/server.py` for that config on a loopback
  port (8095 up) and waits until `/health` says loaded. `Strata:Serve:MaxLoaded` is 1 by default, so another
  size is a switch — the first is stopped before the second loads. `OnDemand` stops an idle one.
- **The coordinator picks**: the console's new **Strata models** panel (Load / Unload / On demand) writes the
  node's profile (`"strata": {"loaded": [...], "onDemand": true}`), so the choice survives a reboot of either side.
- **Installed through the coordinator**: with `HuggingFace:Enabled` the node reports every size Strata knows,
  the panel's **Install on node** (or the routes above) sends the Hugging Face link, and the node runs
  **Strata's own setup** — `setup.py --setup --yes --no-start` — which downloads the pinned files, checks
  them, resumes, and prepares them for that box. One install at a time; byte progress in the command feed.
- The config's own API key is used; `STRATA_DEBUG` never reaches the server and `--api-monitor` is refused —
  no prompt reaches a log (rule 7). `Strata:Serve:Engine=mock` is Strata's own canned engine for checking a
  node's wiring before a 70 GB download.

### Under the hood

The colibri catalogue's admission, LRU eviction, pins and on-demand moved into an engine-neutral
`ModelCatalog`; colibri and Strata are its two users. colibri's behaviour and sentences are unchanged and
its tests ran untouched. The shared contract types were renamed (`NodeCatalogState`, `CatalogProfile`);
the JSON on the wire is the same.

## Measured — a real Strata install on this box (RTX 3090 Ti 24 GB, 256 GB RAM, Windows 11)

Strata `fb58e0d`, its own Windows setup, Coder IQ1_M (58 GB). Hub and node from source:

- The hub's `/api/status` listed `strata-coder-iq1_m` (unloaded) and the nine installable sizes, the Coder
  marked installed.
- A chat through the hub launched `server.py --engine strata --config …strata-coder-iq1_m.json --host
  127.0.0.1 --port 8095`; **loaded in 18.4 s** (23.4 GiB of experts at 6.6 GiB/s, 8 250 experts / 15.7 GiB
  in the GPU cache); "What is the capital of France?" → **"Paris"**, HTTP 200 in 29 s including the load.
- A streamed request: first byte in 46 ms, a correct `zebracorn[::-1]`, 148 tokens in 16 s (**~11 tok/s**,
  expert cache 95 % hit). Strata's README measures 55 tok/s for the Coder on an RTX 5070; this box's number
  is what it is and was not investigated.
- Neither the hub's nor the node's log contains any prompt text (grep for both prompts: 0 lines).
- **An install through the hub**: `POST …/strata/install {"model":"strata-q2_0"}` → 202, the node ran
  Strata's setup, its download bar arrived on the hub's `model-progress` feed as percent; **a chat sent
  during the install was answered in 3.9 s**. Setup ran all seven steps (download, pack, MTP layer, start
  script) and the node logged `Strata 'strata-q2_0' is installed and listed`; the hub listed it at once.
  A size Strata does not have (`strata-swift-iq3_s`) → 400 with the nine names.

## Not established

- AMD, Linux and multi-GPU Strata — only Windows + one NVIDIA card was run.
- Two Strata models loaded at once (`MaxLoaded` > 1): one 3090 Ti holds one.
- Pictures: Strata's vision is a setup choice; the node declares chat only.
- No image carries Strata (its engine is a CUDA/HIP build per card generation, and it ships its own
  Dockerfile).
- Throughput under contention: the published-binary chats below shared the card with a 23 GB Ollama
  model another program on this box reloaded mid-check (GPU at 100 %), so their 0.2–1 tok/s says nothing
  about Strata or InferHub.

## The published artifact

Docker Desktop's VM could not be brought back on this box (WSL itself stopped answering), so instead of a
container the check pulled `inferhub-coordinator:3.64.0` and `inferhub-node:3.64.0` (linux/amd64) from GHCR
anonymously, extracted their `/app` layers and ran **those DLLs** natively — the only way a node image's
code can reach this Windows Strata install and its GPU anyway. Both images' `org.opencontainers.image.revision`
label is `a91b8c3`, the tag commit.

- The published hub reported `3.64.0`, listed both installed sizes (`strata-coder-iq1_m` and the
  hub-installed `strata-q2_0`), and served the console with the Strata panel.
- `strata-q2_0` — the size installed through the hub — loaded in 69 s and answered "Jupiter" (HTTP 200).
- `POST …/strata/models/strata-coder-iq1_m/load` → 200, the profile carries `"strata": {"loaded":
  ["strata-coder-iq1_m"]}`; the node stopped the idle Q2_0 first, then loaded the Coder (130 s, contended).
- No prompt text in either published process's log; no `server.py` left behind after the node was killed.
