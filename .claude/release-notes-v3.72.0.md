# InferHub v3.72.0 — a free slot for the model

Phase 108. One claim, and it follows straight from v3.71.

- **The hub sends a request to a node whose engine can start it now.** Ollama runs `OLLAMA_NUM_PARALLEL` requests per
  loaded model and queues the rest, and that number defaults to **1** (`envconfig.NumParallel`). Measured on this box
  before writing a line — Ollama 0.34.4, a warm `qwen2.5:0.5b`, six `/api/generate` of 200 tokens sent at once — they
  finished at 0.9, 1.4, 2.0, 2.5, 3.0 and 3.5 s: one after another. Since v3.71 a node hands its Ollama everything the
  hub sends it, and the hub balanced by **jobs per node**. With two nodes holding a model, one answering a chat with it
  and the other busy with a different model, both read load 1, the tie rotated, and every other new chat for that model
  waited inside the first node's Ollama while the second node's copy sat idle.
- **A node now declares how many requests one model runs at once** (`modelSlots` on its registration): `Node:ModelSlots`
  when set, otherwise what the backend declares — an Ollama node reads `OLLAMA_NUM_PARALLEL` from its own environment
  and says 1 when it is unset or unparseable, as Ollama does. Every other backend, and a node with several engines,
  declares nothing.
- **The hub counts its own jobs per node and per model**, and the router puts nodes with a free slot for the requested
  model (or no declaration) ahead of the rest, after the warm-card tier and before least-busy. **A preference, never a
  filter**: when every holder is full the request goes where it went before, and Ollama queues it as it always did.

## What changes with no config changed

| Before | After |
|---|---|
| Two Ollama nodes, one busy with model A, one busy with model B: the next request for A rotated between them | it goes to the node whose A slot is free |
| One node, or a fleet whose backends declare nothing | unchanged |
| Every holder of the model full | unchanged — least-busy picks, the engine queues |
| `/api/status` | `nodes[].modelSlots`, and `nodes[].modelsInFlight` while the hub has jobs out |

Nothing is refused that was answered before. The hub's queue and cloud burst still read `Node:MaxConcurrency` only; a
`503` where Ollama would have answered would be a regression, and `Node:MaxConcurrency` is still the way to make the
hub queue.

## Found by running it

A real coordinator and two real meshed nodes from source (`node-a`, `node-b`), both against the Ollama on this box, so
both declared `modelSlots: 1`. A 3000-token `qwen2.5:0.5b` story went to node-a and a `gemma:2b` story to node-b; with
`/api/status` reading `node-a load=1 {qwen2.5:0.5b: 1} | node-b load=1 {gemma:2b: 1}` before each one, **four
`qwen2.5:0.5b` probes in a row all went to node-b**. With both nodes idle the same probes alternated a, b, a, b, as before.

The first two attempts proved nothing and are worth saying so: "count to 3000" and "repeat apple forever" ended after a
few tokens, so the probes saw two idle nodes. A check whose setup did not hold is not a pass.

Both nodes sat on **one** Ollama here, so node-b's probe still queued behind node-a's story in the same `qwen2.5`
runner. That run proves the routing decision, not the latency it saves, which needs two machines.

## Not established

- **An Ollama whose environment the node cannot see.** A separately installed Ollama service started with its own
  `OLLAMA_NUM_PARALLEL` is declared as 1 unless `Node:ModelSlots` says otherwise. Too low only costs a preference;
  nothing is refused.
- **Work the hub did not send.** A solo client, or a second hub, on the same Ollama is invisible to the count.
- **Model names.** The count keys on the request's name, case-insensitive, with a bare name equal to its `:latest`. An
  alias Ollama resolves some other way counts as its own model.
- **Two machines.** The latency saved was not measured.

## Tests

`ModelSlotRoutingTests` (Coordinator): the busy-with-this-model node loses to the busy-with-another-model one every time;
no declaration, all-full and a single node route as before; a sticky conversation leaves a node whose slot is taken;
the counters release and a gone node forgets them; `Dispatcher.ModelOf` reads only the top-level `model`. Removing the
tier turns four of them red. `ModelSlotsTests` (Node): Ollama's parse and the validation. `ConcurrentJobsMeshTests`
(Mesh): the declaration through a real registration (the operator's number wins; red with the registration line
reverted), a blocking and a streamed job holding their models' slots until their answers arrive, a cancelled job giving
its slot back.

`dotnet test InferHub.sln`: **2 292 passed / 63 skipped**. Zero new `PackageReference`; `InferHub.Shared.csproj` is
still empty.
