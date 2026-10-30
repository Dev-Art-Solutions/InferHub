# InferHub v3.83.0 — the hub keeps what it relayed

Phase 123, continuing 3.82. Since 3.82 every byte a sealed node downloads crosses its coordinator, and without a cache
it crosses again for the next node: ten sealed nodes given `qwen3:8b` pull 5.2 GB ten times over the one uplink the
hub has. `Relay:Cache` keeps each blob the relay passed on, so the next node's pull comes from the hub's disk.

```bash
# on the coordinator
Relay__Enabled=true
Relay__Cache__Enabled=true          # off by default
Relay__Cache__MaxGiB=200            # least recently served goes first
Relay__Cache__KeepFreeGiB=20        # never fill the disk past this
```

Nodes need nothing new. A 3.82 sealed node gets the cache the day its hub turns it on.

## What it does

- **It keeps a blob by the sha256 its upstream named, and nothing else.** An Ollama registry blob is addressed by its
  digest. A Hugging Face LFS file is named by the hash Hugging Face sends on the resolve, `X-Linked-Etag`. This was
  checked live on Hugging Face's Xet storage: the 302 still carries it. Manifests, model pages and the catalogue
  change and are small, so they are always relayed live.
- **The upstream is still asked every time.** The relay now follows redirects itself. The first hop always goes to
  Hugging Face or the registry, with the node's own token, and it decides two things: whether this node may have the
  file (a gated repo), and what `main` points at today. Only the body behind the redirect can come from the cache. A
  node without access to a gated repo does not get it because another node downloaded it, and a cached copy can never
  be an old version. The node's token still reaches the upstream's own host and never the CDN, as in 3.82.
- **It is hashed while it is written** and kept only if the bytes match. A mismatch is relayed as before (the node's
  own check refuses its copy) and counted as `discarded`, never kept. A restart drops half-written files.
- **Ollama pulls in parts, so the hub fetches the whole blob once, beside them.** The node's ranges are relayed as
  before, so the first node never waits for a download it did not ask for. A whole download (a Hugging Face file) is
  kept as it passes. One fill per blob at a time.
- **Bounded.** `MaxGiB` (200) evicts the least recently served blob first, and that order survives a restart. A blob is
  not kept if it is bigger than the cache, if its length is unknown, or if keeping it would leave less than
  `KeepFreeGiB` (20) free.
- **Visible.** Answers carry `X-InferHub-Relay-Cache: hit` or `miss` on a blob the cache could hold. `/api/status` gains
  `relay.cache` (entries, bytes, maxBytes, hits, misses, filled, discarded, skipped, servedBytes), and `/metrics` gains
  `inferhub_relay_cache_*`. Both appear only while the cache is on.
- **Rule 4's sixth exception.** It writes under `Relay:Cache:DataDirectory` (`./data/relay`; the image sets
  `/data/relay`). Deleting the directory costs a download, never a wrong answer.

## Upgrading

Nothing changes unless you set `Relay:Cache:Enabled`. With it off there is no directory, no header, no status field
and no series. The relay streams as in 3.82, except that it follows the upstream's redirects itself instead of the
HTTP handler doing it. `Authorization` is dropped when the host changes, exactly as .NET's handler did.

## Not established

- **A real fleet of several sealed nodes.** The live check used one sealed node pulling twice (deleting the model in
  between). Two nodes pulling the same blob at the same moment are covered by the single-flight test, not by a live run.
- **A gated Hugging Face repo with a real token.** The refusal of a first hop over a held blob is a mesh test with a
  fake upstream. The live Hugging Face file was public.
- **A signed CDN URL expiring mid-fill.** The background fill restarts from the first hop. A 20 GB blob whose CDN
  link lapses after the download has started was not tried.
- **Eviction under a real model's size.** Eviction order and the free-disk floor are unit-tested with small blobs.
- **The HA pair.** Each coordinator keeps its own cache. A promoted standby starts cold.

## Verification

- Suite: 2 653 passed, 63 skipped (the gated ones, as before). New: `RelayCacheTests` (18), `RelayCacheMeshTests` (5,
  real Kestrel hub and upstream with a counting CDN). `SealedRelayMeshTests` now builds its hub through `AddNodeRelay`,
  so it runs the redirects-by-hand client.
- Found by the suite, not by the code: editing the coordinator's `appsettings.json` made the incremental build copy it
  over the node's copy in `tests/InferHub.Tests.Node/bin` (both projects ship a file by that name). Two
  `ChatGptBackendTests` then read the coordinator's `ChatGpt:DataDirectory` and failed. Touching the node's file
  restored it. That is a build-order hazard in the test project, not a code change, and a clean build (CI) is
  unaffected.
- Live, from source, on this box. A hub with `Relay:Enabled` + `Relay:Cache:Enabled`, a sealed node (the node build is
  unchanged since 3.82) and the real Ollama 0.40.12:
  - `POST /api/admin/nodes/{id}/models/smollm:135m/pull`: 16 s. Ollama asked for every layer in a range (206, even for
    one of 89 bytes), so all 4 layers were fetched whole beside it. The 488-byte config came as a plain 200 and was
    kept as it passed. `relay.cache`: 5 entries, 91 739 413 bytes, 5 filled, 0 discarded.
  - `ollama rm smollm:135m`, then the same pull: **4 s**. All five blobs came `from the cache` (the hub's log says so per
    blob), `hits: 5`, `servedBytes: 91 739 413`. Ollama verified every digest itself, and a chat through the hub
    answered.
  - A real Hugging Face file through the relay (`bartowski/SmolLM2-135M-Instruct-GGUF`, Q4_K_M, 105 454 432 bytes):
    the first GET was a `miss` and took 139 s, the second was a `hit` and took **0.63 s**. Both copies hash to the
    `X-Linked-ETag` Hugging Face sent (`2e8040ce…`). A 4-byte range from the cache read `GGUF`.
  - `/metrics` showed the nine `inferhub_relay_cache_*` series. They were absent with the cache off (mesh test).
