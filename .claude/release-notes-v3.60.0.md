# InferHub v3.60.0 — ollama, llama.cpp and colibri on one node, and the coordinator decides which are running

Until v3.59 a node ran exactly one backend. A box with an Ollama, a GGUF you wanted to serve with
llama.cpp, and a colibri MoE had to run three nodes, and nothing on the hub could switch any of them
off without somebody logging into the box.

v3.60 lets one node run several engines at once, by name, and lets the coordinator start and stop
each of them.

## What's new

### `Backend:Engines`

```jsonc
"Backend": {
  "Engines": {
    "ollama":  { "Type": "ollama" },
    "qwen":    { "Type": "llamacpp",
                 "Serve": { "Executable": "/opt/llama/llama-server",
                            "Model": "/models/qwen2.5-7b-instruct.Q4_K_M.gguf" } },
    "nomic":   { "Type": "llamacpp", "Embeddings": true, "Autostart": false,
                 "Serve": { "Executable": "/opt/llama/llama-server",
                            "Model": "/models/nomic-embed-text.gguf", "Port": 8081 } },
    "colibri": { "Type": "colibri", "Autostart": false }
  }
}
```

- **One node, every engine's models.** The node sends each request to the engine that reported its
  model. Clients change nothing. Each model is declared under its own engine's capabilities: a
  colibri model is `chat` + `score`, an embedding `llama-server` is `embed` only. So the hub refuses
  an embedding request for a colibri model before it leaves the hub.
- **The node launches `llama-server`** when `Serve:Model` names a GGUF: on loopback, with the file
  name as the model name (`--alias`), and relaunched if it exits. colibri is launched through
  `Colibri:Serve:Model`, as in v3.58. An engine with a `BaseUrl` instead is one that is already
  running next to the node.
- **`llamacpp` is also a single backend type** (`Backend:Type=llamacpp`): the OpenAI dialect at
  `127.0.0.1:8080/v1`.
- **The rules.** At most one `ollama` and one `colibri` engine, because each one is its own
  configuration section. `-v` / `--verbose` in a `llama-server`'s arguments is refused at startup,
  because those flags log prompts and the node logs that process's output. `Backend:Type` next to
  `Engines` may only be the default `ollama`; on the `:colibri` image set `Backend__Type=`.
- **A node without `Engines` is unchanged.**

### The coordinator starts and stops engines

- `POST /api/admin/nodes/{id}/backends/{name}/start` and `…/stop`, and an **Engines** panel on
  `/console.html` with a Start/Stop button per engine.
- **Stopping an engine:** its models stop being routed at once; requests already running get
  `Backend:StopDrain` (30 s) to finish; then a launched process is killed. Stopping an Ollama engine
  unloads only the models this node sent to it, not the rest of that Ollama's models.
- **It is written to the node's profile**, so a stopped engine stays stopped after either side
  restarts.
- **The node's list is the limit.** The hub can start an engine the node lists with
  `Autostart: false`, and stop any of them. It cannot add an engine or name a binary, a model file or
  a port. A name the node does not list is refused by the node itself, whatever the hub sent.
- **Every engine reports its state** on `/api/status` under `engines`: `running`, `starting`,
  `stopped`, `unreachable` or `failed`, with the reason. One engine going down withdraws only that
  engine's models.

## Verified against real engines

Hub and node from source on Windows, CPU only. One node with four engines: the local **Ollama 0.34.4**
(`qwen2.5:0.5b`), two **`llama-server` b11417** (official Windows CPU build) launched by the node
(`qwen2.5-0.5b.gguf` and `nomic-embed.gguf`, the same GGUF blobs Ollama holds), and **colibri v1.12.1**
(OLMoE int8, the published `3.59.0-colibri` image run as a bare engine on port 18000).

- `/api/status` listed all four engines; `ollama` and `qwen` running, `embed` and `colibri` stopped
  (`Autostart: false`).
- Chat through the hub to `qwen2.5:0.5b` (Ollama) and to `qwen2.5-0.5b` (llama-server): both 200.
  Streaming to llama-server: SSE chunks. llama-server accepts the shared client's chunked request
  body (colibri needed a fix for that in v3.58; llama.cpp did not).
- `POST …/backends/embed/start` → the node launched the second `llama-server`, the hub routed
  `/v1/embeddings` for `nomic-embed` to it: 200 with a vector.
- `POST …/backends/qwen/stop` → that `llama-server` process was gone; the hub then answered a chat
  for `qwen2.5-0.5b` with **404 model not found**.
- `POST …/backends/nonsense/start` → **404** naming the four engines the node has.
- `POST …/backends/ollama/stop` → Ollama's `/api/ps` went from `qwen2.5:0.5b` to empty. Started
  again → routed again.
- `POST …/backends/colibri/start` → `olmoe` appeared under `chat` and `score`. Chat 200; `/v1/brio`
  200 (`"no"`, p = 0.93); `/v1/embeddings` for `olmoe` → **404 before the hop**.
- Restarting the node: it came back with the profile's state (`qwen` stopped, `embed` running),
  without being told again.
- No prompt text in the node's or the hub's log.

### What the live run found and fixed before release

1. **A node that was killed (not stopped) left its `llama-server` running**, still holding its port
   and memory. On Windows every launched engine now joins one Job Object with
   `KILL_ON_JOB_CLOSE`. After the fix: kill the node with `Stop-Process -Force` → 0 engine processes
   left.
2. **A booting node started an engine its profile had stopped**, loaded the weights, and killed it a
   second later when the profile arrived. A node connected to a hub now waits up to 15 s for its
   profile before starting the `Autostart` engines. After the fix: the stopped engine is never
   launched on boot.

## Not established — said out loud

- **A bare-metal Linux node killed with `kill -9`** still leaves its launched engines running.
  Linux has no Job Object; the usual fix (`PR_SET_PDEATHSIG`) is not available through
  `Process.Start`. In a container they die with the container.
- **No GPU run.** Every engine ran on the CPU. Two engines sharing one card is untested, and
  `Node:OnDemand` (phase 85) governs only the Ollama engine, not a launched `llama-server`.
- **No image ships `llama-server`.** The plain node drives one running next to it, and the node
  launches one you install. A bundled llama.cpp image is a separate piece of work.
- **A model name two engines both report** goes to the engine whose name sorts first. The order in
  the configuration file is not visible to the process, because .NET configuration returns keys
  sorted. Logged once; give one of them a different alias.
- **colibri's `KvSlots`** is not folded into `Node:MaxConcurrency` on a multi-engine node (it is on a
  colibri-only node). Set `Node:MaxConcurrency` yourself.
- `ToolUploadTests.TheNodeEnforcesItsOwnCeilingEvenWhenTheHubAcceptedTheUpload` failed once in a
  full local run (connection reset while the client was still writing an over-limit upload) and
  passed 3/3 on its own. Not touched by this phase.

## Upgrading

Nothing to do. A node without `Backend:Engines` is the v3.59 node. A v3.60 node against an older hub
runs its engines by `Autostart` (the hub has no `ReportBackendState` and no start/stop routes); an
older node against a v3.60 hub shows no `engines` block, and the start/stop routes answer 409.

Tests: 1 832 passed, 61 skipped (`EngineTests` 27, `EngineMeshTests` 2 new).
