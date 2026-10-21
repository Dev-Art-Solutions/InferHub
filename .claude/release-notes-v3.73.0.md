# InferHub v3.73.0 — slots the engine reports

Phase 109. v3.72 taught the hub to prefer a node with a free slot for the requested model, with one number per node,
and only an Ollama node had one. This release lets an engine that can be asked say it per model.

- **A llama.cpp model says how many requests it runs at once.** `llama-server` picks its slot count itself (`-np`
  defaults to `-1`, auto) and reports it: on this box, b11417, `GET /props` answers **`total_slots: 4`** for
  `qwen2.5-0.5b`. A llama.cpp engine now asks each **loaded** model and the number travels with that model to the hub
  (`slots` on the node's model report, absent when nobody answered). The hub uses it before the node's own number.
- **The question loads nothing.** A llama.cpp router loads any model a request names (`models_autoload: true`), and
  `/props?model=X` is a request — asked plainly, it would load an unloaded model. The node asks with
  `&autoload=false`, which answers `400 model is not loaded` and leaves it unloaded (measured). An unloaded model has no
  number until it loads. The answer is kept while the model stays loaded and forgotten when it unloads.
- **A node with several engines declares its Ollama's number** as the default for models nothing answered for.
  `Node:ModelSlots` still overrides that default; an engine's own answer overrides both.
- **A warm or an unload re-reports the node's models at once**, as a pull or a delete has since v3.61, so the hub hears
  a freshly loaded model's number without waiting up to a minute for the next refresh.

## What changes with no config changed

| | Before | After |
|---|---|---|
| A llama.cpp model on a node | counted as "no opinion" — routed by load alone | counted against its own `total_slots` |
| A node with an Ollama and a llama.cpp engine | declared nothing | declares its Ollama's number; loaded llama.cpp models their own |
| A warm or an unload from the hub | the model list reached the hub at the next refresh (≤ 60 s) | at once |
| `/api/tags`, the fleet model list in `/api/status` | — | unchanged; one node's count is not the fleet's |
| `/api/status` | — | adds `nodes[].slotsByModel` |

Nothing is refused that was answered before, as in v3.72: a slot count is a routing preference.

## Found by running it

A real coordinator and one node from source with **both** engines: the Ollama on this box, and a llama.cpp router the
node launched over `C:/tmp/llr` (one GGUF, `qwen2.5-0.5b`, unloaded).

- The node registered with `modelSlots: 1` (its Ollama's) and no `slotsByModel`: nothing loaded, nothing asked.
- `POST /api/admin/nodes/{id}/models/qwen/warm` → `slotsByModel: {qwen: 4}` at the hub **2.5 s later**.
- `…/unload` → `slotsByModel` gone; then **65 s — a full refresh interval — and the router still listed `qwen` as
  `unloaded`**: the node's listing never loaded it.
- `/api/tags` had no `slots` anywhere.

My first start of that node failed validation — I had set `Backend:Engines:ollama:BaseUrl`, which an Ollama engine
does not read (its address is `Ollama:Endpoint`), and the node said exactly that and refused to start.

A side observation while hunting a stray process: **Ollama 0.34's own runner is a `llama-server` started with
`-np 1`** — the same one-per-model fact v3.72 measured, from the other side.

## A teardown race the full suite found

`StrataMeshTests.TheHubRoutesAStrataModelInstallsAnotherFromALinkAndSwitchesToIt` failed once in the first full run —
in its **teardown**, deleting the test's directory: `strata-iq2_xs.json` was held by another process. A model command
runs off SignalR's dispatch (since 98), and since this release a switch re-reports the models, which re-scans the
catalogue's configs; a scan could still have a file open just after the node stopped. It passed five runs of its own,
and the same test had failed once before, in v3.71's CI. Production never deletes a live catalogue directory, so the
teardown now retries the delete for up to two seconds on a sharing violation. Recorded as made likelier by this phase,
not as unrelated.

## The published binaries — and the one claim they corrected

The published `inferhub-coordinator:3.73.0` and `inferhub-node:3.73.0` images (revision label `b19d93c`, the tag
commit) were pulled from GHCR and their `/app` binaries run natively, the node with the same two engines as above. The
node registered as `3.73.0` with `modelSlots: 1`; a warm showed `slotsByModel: {qwen: 4}` **2.6 s** later; `/api/tags`
carried no `slots`; after 65 s the router still listed `qwen` as `unloaded`; no exception in either log.

**The unload did not do what this file said.** Twenty seconds after `…/qwen/unload` the hub still read
`{qwen: 4}`, and it cleared only at the next refresh. b11417 answers `POST /models/unload` while the instance is still
stopping (`unload: stopping model instance` in its log), so the re-report that followed listed the model as `loaded`.
The run from source had cleared within twenty seconds by timing alone. A stale count is a routing preference for up
to a minute, never a refusal — but "a warm or an unload re-reports at once" was true of the warm only. **Fixed in
v3.73.1.**

## Not established

- **llama.cpp builds other than b11417.** `total_slots`, the router's `/props?model=` and `autoload=false` are read
  from that build's answers.
- **Routing across two llama.cpp nodes live.** The live run had one node; choosing between nodes by these numbers is
  v3.72's tier with a different number fed in, and is covered by `ModelSlotRoutingTests`.
- **A preset that sets its own `--parallel`.** Read the same way, and re-asked after a reload; not run.

## Tests

`LlamaCppRouterTests` (Node): a loaded model is tagged with its `total_slots` and the stub's call log shows an unloaded
one is never asked and the question carries `autoload=false`; the answer is kept while loaded and asked again after a
reload; a 400 is no answer; a single-model engine asks `/props`; a node with both engines keeps llama.cpp's number and
defaults to its Ollama's. `ModelSlotRoutingTests` (Coordinator): a model's number beats the node's (red with that
reverted), it stays out of the fleet list, and `slots` is absent from the wire when null. Removing the tagging turns
three node tests red.

`dotnet test InferHub.sln`: **2 307 passed / 63 skipped**. Zero new `PackageReference`; `InferHub.Shared.csproj` is
still empty.
