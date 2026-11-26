# InferHub v3.115.0: picture and video models from a link

Phase 121 closes the "models that fit" track (118–121). Since 3.79 a node answers a pasted link to a text model with what
its engines can do. Now it answers a link to a picture or video model too. These are diffusers repositories, the ones
with a `model_index.json`, and the node answers from its image and video recipes.

```http
POST /api/control/fit          {"link": "Lykon/dreamshaper-8"}
POST /api/control/fit/install  {"link": "Lykon/dreamshaper-8", "engine": "diffusion"}
```

## What changed

- **A recipe that pins the repository is the option as it stands.** The SDXL base repository is `sdxl`. The install is
  phase 48's pull of that recipe.
- **A repository whose pipeline class a recipe here runs gets a recipe written from that one.** This is the common case:
  a fine-tune.
  - Copied from that recipe: the pipeline, sizes, steps, dtype and quantization.
  - Taken from this repository: the commit it is at now, its licence, and its weights (the fp16 files when every part
    has them).
  - VRAM is the template's figure, scaled by the weights' bytes.
  - When the card names a base model a recipe pins, that recipe is the template. Otherwise it is the one closest in
    size, then the one with more default steps.
- **Said, not offered:**
  - a pipeline no recipe here runs;
  - a repository with Python code in it;
  - a component whose weights are only pickles (`.bin`).

  A recipe the node writes sets `useSafetensors`, so diffusers cannot fall back to a pickle.
- **Each of the owner's switches is named in the answer:**
  - the `diffusion` tool running;
  - `Tools:AllowModelDownload`;
  - the licence in `Tools:Image:AcceptedLicenses`, unless it is permissive;
  - `Tools:Image:HuggingFaceToken` for a gated repository.
- **Nothing is written by looking.**
  - The recipe is written at install, to the new `Tools:Image:AddedRecipeDirectory`. Unset, it is
    `<Node:DataDirectory>/recipes`, which is `/data/recipes` on the container, so an update keeps it.
  - The node's catalogue and the worker read both directories. The shipped one wins a clash.
  - The worker weighs a recipe written after it started when it next declares.
  - From the coordinator (`Node:Management`, v3.81), the recipe is written only after the owner's disk terms allow it.
    The hub's install pull now carries its tool.
- **Fixed: a `:diffusion` node could not turn `HuggingFace:Enabled` on.** It refused to start with "nowhere to put a
  model". A node running the `diffusion` tool now has a place.

One new configuration key (`Tools:Image:AddedRecipeDirectory`), zero new packages, `InferHub.Shared.csproj` still empty. A
deployment that changes no config behaves the same, except that a link to a diffusers repository now gets an answer.

## What was run

The published 3.114.0 `-diffusion` image with this release's node and worker on top, on an RTX 3090 Ti, with
`Node:Vram:BudgetMiB` 6000:

| Link | Answer |
|---|---|
| `Lykon/dreamshaper-8` | a recipe from `sd15` (2034 MiB of fp16 weights, as sd15's → 4000 MiB), in 3 s; installed; a 512×512 picture in 5 s, the card +3437 MiB |
| `Lykon/dreamshaper-7` | through a coordinator from source: estimated, installed by the hub, generated through the hub in 11 s |
| `RunDiffusion/Juggernaut-XL-v9` | a recipe from `sdxl`, chosen by the card's base model; 8000 MiB, does not fit a 6000 MiB budget |
| `stabilityai/stable-diffusion-xl-base-1.0` | the shipped `sdxl` recipe as it stands |
| `Kwai-Kolors/Kolors-diffusers`, `stabilityai/stable-cascade` | said: no recipe here runs `KolorsPipeline` / `StableCascadeDecoderPipeline`, with the pipelines that are run |

The written recipe survived the container's re-creation and was declared at the next start.

Found by that run, and fixed before the tag:

- The `HuggingFace:Enabled` refusal above.
- A rule that refused every component library but diffusers and transformers. It refused Kolors and Stable Cascade for
  code they do not carry: `kolors` and `wuerstchen` are diffusers' own modules. Code from a repository is a `.py` file,
  and that is what is refused now.

## What was not established

- A video fine-tune written and run.
- A quantized template (flux, qwen) scaled.
- A gated repository end to end. FLUX.1-dev answers 401 to the lookup without `HuggingFace:Token`, and the node says so.
- Found, not changed: a networked node whose language backend lists no models never sends its model report, so its
  tools' capabilities do not reach the hub either. An example is a `:diffusion` image with no Ollama. Solo mode is
  unaffected; the lab pointed the node at an Ollama.
