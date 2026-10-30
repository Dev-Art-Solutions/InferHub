# InferHub v3.84.0 — models supplied through the coordinator

Phase 124, continuing 3.82 and 3.83. A sealed node downloads through its hub, and since 3.83 the hub keeps what it
relayed. But the hub was still only a relay. A model was fetched at the moment a node needed it, every pull asked the
upstream, and a hub with no internet had nothing to relay. An air-gapped site could not manage models at all.
`Relay:Supply` makes the hub the fleet's source. An admin fetches a model once, or imports it from a disk, and the
hub answers its sealed nodes from its own disk without asking anybody.

```bash
# on the coordinator
Relay__Enabled=true
Relay__Supply__Enabled=true             # off by default
Relay__Supply__Only=true                # optional: nothing that is not supplied, ever
Relay__Supply__ImportDirectory=/import  # where a carried-in disk is mounted
```

```http
POST /api/admin/supply         {"ollama": "qwen3:8b"}
POST /api/admin/supply         {"huggingFace": "owner/repo", "revision": "main", "include": ["*Q4_K_M.gguf"]}
POST /api/admin/supply/import  {"path": "disk1", "name": "qwen3:8b"}
GET  /api/admin/supply
DELETE /api/admin/supply/{id}
```

Nodes need nothing new. The hub answers the paths a 3.82 sealed node already asks through the relay.

## What it does

- **Fetched once, and every byte checked.** A fetch is a job (202, then `GET /api/admin/supply/jobs/{id}`). It reads
  from `Relay:OllamaRegistry` and `Relay:HuggingFace`, so a mirror beside the hub works. Each Ollama blob is checked
  against its digest (and the manifest against `Docker-Content-Digest`), each Hugging Face LFS file against its sha256,
  each small file against its git oid, and every size. A model is supplied whole or not at all. A file that does not
  match fails the job and is deleted, and a record is written only when every blob is on disk.
- **Imported offline, in layouts the internet side already has.** `POST /api/admin/supply/import` reads a directory
  under `Relay:Supply:ImportDirectory`. It accepts an Ollama models directory (`~/.ollama/models`), a Hugging Face
  cache (`models--owner--repo`, as `huggingface-cli download` leaves it), or another hub's supply directory. Each file
  is hashed as it is copied. The source is left as it was. A path with `..`, a rooted path or a drive is a 400.
- **The upstream is not asked.** This is the difference from the cache, and it is on purpose. The admin's fetch was the
  decision, so a supplied model is served as it was on the day it was fetched: `main` stays the commit it pinned, and
  `qwen3:8b` stays that manifest. Fetch it again to update it; blobs nothing else needs are then deleted.
- **`Only` closes the last door.** With `Relay:Supply:Only=true`, anything not supplied is a 404 that names the key,
  and the hub fetches nothing for a node. The node's Hugging Face client and its loopback registry read the
  `not-supplied` header, so a pull ends with the hub's sentence rather than Ollama's "file does not exist". The admin's
  own fetch still reaches the upstreams (or the mirror).
- **Every file is checked by the node too.** The tree the hub replays lists every file with the sha256 it was checked
  against, so the node's own check (98) covers a small `config.json` as well as the GGUF.
- **Visible.** Answers carry `X-InferHub-Relay-Supply: supplied`, or `not-supplied` on a refusal. `/api/status` gains
  `relay.supply` (models, bytes, only, served, servedBytes, refused, running) and `/metrics` gains six
  `inferhub_relay_supply_*` series, all only while the supply is on. The console has a **Supplied models** panel: fetch,
  import, the jobs with their progress, and remove.
- **Rule 4's seventh exception.** It writes under `Relay:Supply:DataDirectory` (`./data/supply`; the image sets
  `/data/supply` and `ImportDirectory=/import`). Nothing is evicted, because under `Only` an evicted model would be gone
  from the fleet. A fetch or an import that would leave less than `KeepFreeGiB` (20) free is refused before its first
  byte. A gated repo needs the hub's own token, `Relay__Supply__HuggingFaceToken`.

## Upgrading

Nothing changes unless you set `Relay:Supply:Enabled`. With it off there is no directory, no header, no status field
and no series, and `/api/admin/supply` answers 404 saying it is off. The relay behaves as in 3.83. On `/api/status`,
`relay.cache` is unchanged, and the `relay` block now appears when either the cache or the supply is on.

## Found while building it

- **Ollama 0.40 writes a new pull under `manifests-v2/ollama.com/…`**, not `manifests/registry.ollama.ai/…`. Found by
  looking at this box's real models directory before the live import: `smollm:135m`, pulled two days ago, was only in
  the new tree. The import reads both, and the mesh test runs both layouts.
- **The auto-mode classifier refused `ollama rm`** on the live box, rightly: it would have deleted a model somebody
  uses. So the live pull used a model that was not on the machine (`all-minilm:33m`), which only adds.

## Not established

- **A real air-gapped site.** "Offline" here was a hub whose three upstreams pointed at a closed port, on a machine that
  has internet. The node was sealed, so its own egress was refused by 122's handler, but the machine was not unplugged.
- **A real Hugging Face cache with symlinks.** The live import was an Ollama models directory. The Hugging Face cache
  import is a mesh test with plain files; the symlink-to-`blobs/` check (a file named by a sha256 must match it) is
  code, not a run.
- **A gated Hugging Face repo with the hub's real token.** The token reaching the first hop and not the CDN is a mesh
  test with a fake upstream. The live file was public.
- **A node's Hugging Face pull through the supply, end to end.** The node's own `HuggingFaceClient` (commit, tree,
  download, model info) ran against the supply in the mesh test. Live, the GGUF was read through the relay with curl;
  no node with a GGUF engine pulled it.
- **The console panel in a browser.** It is served and its script parses; nobody clicked it.
- **A big model.** The largest live file was 398 MB. Disk-floor refusals are code, not a run at 20 GB.
- **The HA pair.** Each coordinator holds its own supply.

## Verification

- Suite: 2 682 passed, 63 skipped before the docs, then the slice again after the manifests-v2 fix. New:
  `RelaySupplyTests` (22), `RelaySupplyMeshTests` (8: real Kestrel hub and a fake registry + Hugging Face behind a
  counting CDN; fetched, then served with the upstream stopped; the node's own `HuggingFaceClient` pulling it; `Only`;
  corrupt bytes; both Ollama layouts and a Hugging Face cache imported; off).
- Live, from source, on this box. A hub with `Relay:Enabled` + `Relay:Supply:Enabled`, a sealed node (`Node:Sealed`)
  and the real Ollama 0.40.12:
  - Online, the hub fetched `smollm:135m` (5 files, 91 739 413 bytes) from registry.ollama.ai and
    `bartowski/SmolLM2-135M-Instruct-GGUF` `*Q4_K_M.gguf` (105 454 432 bytes, commit `09816acd`) from Hugging Face,
    each in about 5 s, then `all-minilm:33m` (4 files, 67 319 908 bytes).
  - Restarted **offline**: `Relay:OllamaRegistry`, `HuggingFace` and `OllamaLibrary` all at `http://127.0.0.1:9`,
    `Only=true`. The supply came back from disk (4 models).
  - The sealed node pulled `all-minilm:33m` through the hub's admin API. The manifest and every blob it asked for came
    `from the supply` (the hub's log says so per request; Ollama already had two shared blobs and did not ask for them).
    Ollama verified the digests itself and listed the model, and an embed through the hub returned 384 dimensions.
  - The node asked for `tinyllama:latest`, not supplied. The hub logged `Refused … not supplied (Relay:Supply:Only)` and
    the node's pull failed with *"the coordinator does not supply 'tinyllama:latest': … an admin supplies a model with
    POST /api/admin/supply …"*.
  - `POST /api/admin/supply/import {"path":"Models","name":"qwen2.5:0.5b"}` with `ImportDirectory=D:/Ollama` imported
    the real Ollama directory's `qwen2.5:0.5b` (5 files, 397 821 319 bytes). The model layer read back through the
    relay hashes to its digest `c5396e06…`. `{"path":"../Windows"}` was refused.
  - The Hugging Face GGUF read through the relay with the upstream offline: `supplied`, 290 ms, `X-Repo-Commit`
    `09816acd…`, and it hashes to `2e8040ce…` (the `X-Linked-ETag` 3.83's live check recorded). It starts `GGUF`.
  - `/metrics`: `inferhub_relay_supply_models 4`, `…_served_total 7`, `…_refused_total 1`.

## The published image

`ghcr.io/dev-art-solutions/inferhub-coordinator:3.84.0` carries revision `de5e71a` and sets
`Relay__Supply__DataDirectory=/data/supply` and `Relay__Supply__ImportDirectory=/import`. Docker Desktop was up this
time, so the container itself ran: `Only=true`, all three upstreams at `http://127.0.0.1:9` (the container's own
loopback, closed), and this box's real Ollama models directory mounted read-only at `/import`.

- `POST /api/admin/supply/import {"path":".","name":"qwen2.5:0.5b"}` imported the model (5 files, 397 821 319 bytes) in
  2 s. The container runs as `app`, and `/data/supply/{blobs,models,partial}` were created owned by `app` under the
  image's `chown app:app /data`.
- The sealed node (from source at the tag) pulled `qwen2.5:0.5b` through the container: the manifest came `from the
  supply`, Ollama already held the blobs, and the copy to its own name followed. A pull of `tinyllama:latest` was
  `Refused … not supplied (Relay:Supply:Only)` and the node's pull failed with the hub's sentence.
- The model layer read through the container's relay was `supplied` and hashes to its digest `c5396e06…`.
- `/metrics` (behind the admin key, as host→container is not loopback): `inferhub_relay_supply_models 1`,
  `…_served_total 2`, `…_refused_total 1`.
- Not run against the container: a chat (no `Auth:ApiKeys` was set on it, so inference answered 401, as it should), and
  a fetch (the upstreams were closed on purpose).
