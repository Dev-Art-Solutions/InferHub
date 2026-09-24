# InferHub v3.50.0 — `Node:OnDemand`: one GPU service at a time

Until now a node assumed its card belonged to it. Ollama kept a chat model resident for its own
`keep_alive`, and each tool (Whisper, Piper, diffusion, video) kept a warm worker process. On a
dedicated GPU box that is the right default. On a desktop, where the one card is also the owner's,
it means audio, video and an LLM compete for the same VRAM, and the card is never fully free.

v3.50 adds an opt-in mode for that box.

## What's new

```json
"Node": {
  "OnDemand": { "Enabled": true, "ReleaseAfterSeconds": 30, "SwitchWaitSeconds": 300 }
}
```

or `-e Node__OnDemand__Enabled=true` on any node image. With it on, the local Ollama and every tool
take the card **one at a time**:

1. A request arrives for a service that does not hold the card. It **waits** until the holder's
   in-flight work finishes. Nothing is evicted mid-job.
2. The holder is released. Ollama unloads the models **this node** loaded (`keep_alive: 0`). A tool's
   worker processes are **stopped**, which also frees the CUDA context the phase-48 idle hint leaves
   behind.
3. The newcomer starts, loads its model and runs.
4. Once a service has been quiet for `ReleaseAfterSeconds`, it is released even if nobody else asked,
   so the card is free again. `0` releases the moment the last request ends.

Requests for the service that already holds the card share it, so two chats still run side by side.
Once somebody is waiting for the card, new requests for the holder queue behind them, so a steady
stream of chat cannot starve a video job. A request that waits past `SwitchWaitSeconds` gets the
usual `503` + `Retry-After`.

What the node declares to the coordinator does not change. A stopped tool is started again by its
next request, and it re-reports its models exactly as it does at boot.

| Key | Default | |
|---|---|---|
| `Node:OnDemand:Enabled` | `false` | Off is byte-identical to v3.49. |
| `Node:OnDemand:ReleaseAfterSeconds` | `30` | How long a service keeps the card after its last request. |
| `Node:OnDemand:SwitchWaitSeconds` | `300` | How long a request waits for another service before a `503`. |

## Only the models this node loaded

The first version unloaded everything Ollama had resident. Before the first live run, the real box
had a 22 GB model loaded through the same Ollama by another program. A desktop's Ollama is shared,
and dropping someone else's model makes them pay its load again. So the node records the `model` of
every request it sends and unloads only those. It checks them against `/api/ps` first, treating
`llama3` and `llama3:latest` as the same model, so a model that is not loaded is never loaded just to
be dropped.

## The price is cold starts

Every switch pays a model load: seconds for Whisper or Piper, a minute or more for FLUX or a large
LLM. Alternating services request by request is slow by design. This mode is for a box that does one
kind of job at a time and should be idle in between.

## Verified

- `dotnet test InferHub.sln`: 1,649 passed, 0 failed. New: `GpuArbiterTests` (16: sharing,
  switching order, linger, first-come-first-served, timeout, a failing release, a stream holding the
  card until its last chunk, unload scope) and `OnDemandToolTests` (a real worker process is gone
  after the request, the capability is still declared, the next request starts it again).
- A solo node from source against a real Ollama: `qwen2.5:0.5b` loaded for a chat and was gone from
  `/api/ps` five seconds later. A model another program had loaded stayed resident throughout.

**Not verified:** the tool path with a real Whisper, Piper or diffusion worker on a GPU. It is
covered by the echo worker test above, which is a real child process but not a CUDA one.
