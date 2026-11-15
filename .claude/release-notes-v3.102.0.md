# InferHub v3.102.0: a 70B model no node can hold, answering from three — and a load you can watch

Phase 142, the first of a new track (142–149): shared compute past the 8B lab. Every split-model run so far used an 8B model,
which fits one machine. This release ran a model that does not.

```sh
docker volume create lab-models
docker run --rm -v D:/models:/src:ro -v lab-models:/models inferhub-lab-llama:b12182 cp /src/wizardlm-70b.gguf /models/
LAB_MODEL_DIR=lab-models LAB_MODEL=wizardlm-70b.gguf LAB_GROUP=wizardlm-70b-split \
  LAB_NODE_MEMORY=18g LAB_OFFER_GIB=14 LAB_CPUS=16 LAB_WAIT=3000 LAB_MAX_TOKENS=32 deploy/lab/shards/tunnel-lab.sh
```

## What was measured

WizardLM 70B Q4 (80 layers, 36.2 GiB), three nodes in 18 GiB containers, each offering 14 GiB and 16 CPUs, Docker on one
64-core PC, CPU only. The coordinator assembled the group; the answers came from its `/v1/chat/completions`.

| | Whole model, one container | The group, unshaped | The group, 30 ms and 50 Mbit/s on every node |
|---|---:|---:|---:|
| Generate | 1.87 tok/s | 1.75 tok/s (94 %) | 1.21 tok/s (65 %) |
| Text | — | the same, 72 tokens | the same |

- Layers 0–26, 27–53 and 54–79 + output; each node held 12.4–12.7 GiB.
- Until the group answered: 200 s, with 24.5 GiB sent through the tunnels.
- A reload on the shaped network, with the layers each member had kept: 247 s, and about 270 MiB sent instead of 12 GiB a member.
- The 8B model kept 47 % of its speed at 30 ms (3.101.0). The 70B keeps 65 %: the round trips per token are the same, and
  each token is more computing.

**The track's gate passed.** The track continues with phase 143.

## What changed in the product

- **A loading group says how far it is.** `GET /api/admin/shards/groups` (and `/api/admin/nodes`) now carry, for each member of
  a group, `expectMiB` — its share of the model file — and `sentMiB` — what the head has sent it since the group started. The
  console shows "x of y GiB sent" beside the member's layers while the group is `starting`. The head's own layers carry
  neither: they do not cross the network.
- A head reports this at most every five seconds while a group loads, and only when the count moved.
- Only the node changed. A coordinator passes the state through; a head older than 3.102 sends neither field.

## What changed in the lab

- `deploy/lab/shards/tunnel-lab.sh` takes `LAB_NODE_MEMORY`, `LAB_OFFER_GIB`, `LAB_CPUS`, `LAB_CONTEXT`, `LAB_WAIT` and
  `LAB_MAX_TOKENS`. The defaults are the old values, so `verify-lab.sh` runs as before.
- `LAB_MODEL_DIR` may be a Docker volume's name. **Found on the published 3.101.0 images:** with the 70B bind-mounted from
  Windows, the head paged 13.9 GiB of the file in twelve minutes and had sent no member a byte. From a volume the load is 200 s.

## Context files

`src/InferHub.Node/Backends/CLAUDE.md` was at 1082 of its 1100 lines. The shard phases (133–140) moved, unedited, to
`src/InferHub.Node/Backends/Shards/CLAUDE.md`, which also holds phase 142.

## Tests

`ShardGroupHostTests`: a member's share is its layers' part of the file and the head expects nothing; a starting group reports
what was sent since it started, not since the node did. Whole suite: 2895 passed, 70 skipped, 0 failed.

## Not established

- **GPUs.** Every node ran on CPUs and shared one host's cores and memory bandwidth.
- **A first load at a home uplink's speed.** The first load ran on an unshaped network. 24.5 GiB at 50 Mbit/s is about
  70 minutes; that figure is computed, not measured.
- **A member leaving a group of this size.** The spare and the drain (3.97.0) were not rerun with the 70B.
- **More than three members, models above 70B, several requests at once.**
- **A restarted `llama-server` inside one group** keeps the first load's count, so after a cached reload `sentMiB` is above
  `expectMiB`. The console caps the figure it shows.
- **The console in a browser.** The JSON was read.

## The published images, checked (2026-11-16)

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.102.0` and `inferhub-node:3.102.0`, both labelled revision `2a3b88f`.

- **The 70B group on the published images:** `starting` showed 5.4 of 12.2 GiB sent to the first member after 45 s, 12.1 and
  4.4 GiB after 105 s, both members complete after 165 s; then `running`. Through the coordinator: the same 72 tokens as the
  whole model, 1.68 tok/s (64 tokens in 38.2 s).
- **`deploy/lab/shards/verify-lab.sh 3.102.0`** with `VERIFY_REVISION=2a3b88f…` (the 8B lab of 3.100.0, unchanged defaults):
  28 checks of 28 held — the private group, a member leaving, the community group, the relay.
- All three workflows finished green; the Windows setup and its checksum are attached to the release.

Not rerun on the published images: the 30 ms profile and the cached reload (measured from source, on the same commit).
The blog post is not published yet: the blog connector needed authorization on the day. The draft is `.claude/blog-v3.102.0.md`.
