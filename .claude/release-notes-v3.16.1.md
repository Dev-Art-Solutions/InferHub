# InferHub v3.16.1 — `:latest` was the 12 GB image, and had been for nine releases

**No code change.** One line of CI, and it is worth a release because the artefact it fixes is the
one somebody gets when they type the shortest possible command.

## What was wrong

`docker pull ghcr.io/dev-art-solutions/inferhub-node` — no tag — handed you
**`inferhub-node:diffusion`**: 12 GB, `linux/amd64` only, and it refuses to start without a CUDA
device. Every document in this repository says the plain node is ~340 MB and multi-arch, and the
image chooser in the README says in so many words *"do not pull `:tools` for chat"*.

## Why

`docker/metadata-action` defaults to `latest=auto`, which adds a `latest` tag on any semver push.
The suffix set by `flavor: suffix=-diffusion` does **not** apply to it — `onlatest` is false by
default — so every suffixed matrix entry was quietly pushing a **bare `:latest`** alongside its own
tags.

Four jobs in one matrix, all writing `ghcr.io/…/inferhub-node:latest`, and the winner was whichever
finished last. In practice that is always the diffusion image, because it is by far the slowest to
build. Before v3.14 it was the tools image; before v3.10, the bundled one. The plain node has
probably not owned its own `latest` tag since **v3.7**.

`latest=false` on the three suffixed entries. `:latest` now belongs to exactly one job.

## How it was found

By asking the registry what the tags actually point at after tagging v3.16.0 — not by reading the
workflow, where it is invisible, and not by any test. `:latest` and `:3.16.0-diffusion` resolved to
the same digest while `:3.16.0` resolved to a different one, which is a sentence with only one
possible meaning.

That is the **seventh** time in this project's history that pulling the published artefact found
something a green suite could not (v2.5.1, v3.0.1, v3.5.1, v3.10.0, v3.14.0, the phase-32 D7 note).
It is a step in the release ritual rather than a suggestion for exactly this reason.

## What to do

Nothing, unless you pull untagged. If you do, and you have been getting a surprisingly large
download, that was this. After v3.16.1 the tags are:

| Tag | What it is |
|---|---|
| `inferhub-node`, `:latest`, `:3.16.1` | The plain node, ~340 MB, amd64 + arm64 |
| `:3.16.1-ollama`, `:ollama`, `:gpu` | Ollama inside, ~4 GB, amd64 |
| `:3.16.1-tools`, `:tools` | The above plus Whisper and Piper, ~6 GB, amd64 |
| `:3.16.1-diffusion`, `:diffusion` | The plain node plus PyTorch and six recipes, ~12 GB, amd64 |

`dotnet test`: 1141 passed, 0 failed, 46 skipped — unchanged, because nothing in `src/` moved.
