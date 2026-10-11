# InferHub v3.67.0 — hard limits per runtime: the node sets the ceiling, the coordinator may only go lower

Until now a node could not say "llama.cpp may use 16 GB and eight cores, Strata may use 20 GB of the card
and no more". Phase 82's `Node:ResourceLimits` is a whole-box *soft* cap — the node stops taking new work
while it is over — and its one hard mechanism was a Windows-only CPU rate cap on tool workers. Nothing
capped a runtime's memory, VRAM, cores or GPUs, and the coordinator could neither see nor narrow any of it.

## What's new

**`Node:ResourceLimits:Engines`** — hard limits per runtime, over **every process of that runtime
together** (three `llama-server`s share one `llamacpp` budget; Ollama's runners count against `ollama`):

```jsonc
"Node": { "ResourceLimits": { "Engines": {
  "llamacpp": { "CpuPercent": 50, "Cores": 8, "MemoryMiB": 16384 },
  "strata":   { "VramMiB": 20000, "Gpus": "0" },
  "tools":    { "CpuPercent": 25, "Gpus": "none" }
} } }
```

Runtimes: `ollama`, `llamacpp`, `colibri`, `strata`, `tools` — any other name fails startup, because a limit
written against a typo is a limit that silently does not exist. Every field unset = no limit; an empty
`Engines` is v3.66 exactly (no OS object is made, no environment touched).

**How each is held — and the node reports which, never "held" when it is not:**

| Limit | Windows | Linux |
|---|---|---|
| `cpuPercent` | Job Object CPU hard cap | cgroup v2 `cpu.max` if this process may make one, otherwise reported **not held** |
| `cores` | Job Object affinity | `sched_setaffinity` on every thread, re-applied each poll |
| `memoryMiB` | Job Object job memory limit — an allocation past it fails | cgroup `memory.max`, otherwise watched and the runtime stopped above it |
| `vramMiB` | watched via the `GPU Process Memory` performance counters, stopped above it | watched via NVML, stopped above it |
| `gpus` | `CUDA_/HIP_/ROCR_/GGML_VK_VISIBLE_DEVICES` at launch; a change restarts the runtime | same |

No operating system caps GPU memory, so VRAM is a watchdog: the runtime is stopped, its own supervisor
starts it again (95's backoff, 36's restart), and the node counts the breach with the numbers. The Windows
limits are re-set live on processes already running; the per-runtime Job Object nests inside the phase-95
lifetime job, so killing the node still takes its engines with it. Only processes **this node starts** are
held — an Ollama running as its own service, or somebody else's server, is not ours to cap (36 D1).

**The coordinator sees the limits and may only go lower.** A profile's new `limits` block, the console's
new **Runtime limits** panel (*Set…* / *Clear hub's* / *Limit a runtime…*), or
`PUT /api/admin/nodes/{id}/limits/{runtime}` with `{"cpuPercent": 30, "vramMiB": 12000}` (`DELETE` takes
the hub's back, `GET` returns the node's report). Per field, in force is the smaller of the node's and the
hub's. A value above the node's is a **409 naming the node's number**; a profile written around the API is
refused **by the node** (43 D1: the clamp that matters runs on the box), which keeps its own value and
reports the refusal. A field the node left unset the hub may set to anything — limiting is narrowing. A GPU
list must be a subset of the node's.

`/api/status` carries `nodes[].limits` — per runtime: the node's ceiling, the hub's request, what is in
force, how each is held, processes, CPU %, memory, VRAM, breaches and the last one as a sentence.

**On the box:** `/api/admin/engine-limits` (with `LocalApi:Enabled=true`, loopback only) shows the same and
edits the ceiling into `node.local.json` — in force within one `PollInterval`, no restart, validated before
it is written. Phase 82's soft-cap page now keeps those limits when it saves.

The wire change is additive: a `ReportLimitsState` hub method and an optional `limits` field on a profile. A
v3.66 hub ignores the report; a v3.66 node ignores the field — so a hub's limits mean nothing to a node
older than v3.67, and the panel shows such a node as "nothing limited".

Zero new packages: Job Objects, PDH, `sched_setaffinity` and NVML are hand-rolled P/Invoke.
`InferHub.Shared.csproj` is still empty.

## Measured — on this project's box (Windows 11, RTX 3090 Ti, 64 logical processors)

A coordinator and a node run from source, the node launching winget's **Vulkan** `llama-server` with
qwen2.5-0.5b (`-ngl 99`) as a `llamacpp` engine, ceiling `CpuPercent 10, Cores 4, MemoryMiB 4096`:

- The node reported all three as `os (Job Object …)`; Windows itself showed the `llama-server`'s
  `ProcessorAffinity` as **15** (four cores). The coordinator showed the same ceiling in `/api/status`.
- Hub `cpuPercent: 50` → **409** "limits llamacpp cpuPercent to 10% itself … never 50%". `vllm` as a runtime
  → **404** naming the five.
- Hub `cpuPercent: 5, vramMiB: 100` → in force `{5, 4 cores, 4096 MiB, 100 MiB}`. The watchdog read
  **255 MiB** of dedicated GPU memory from the performance counters and stopped the server; the breach was
  counted with that sentence. (`nvidia-smi` on the same box shows **N/A** per process — NVML cannot answer
  this under WDDM, which is why the counters.)
- *Clear hub's* → the node's own limits again, the server running.
- Hub `vramMiB: 2000` → **206 MiB** read, nothing stopped; then `gpus: []` → the server was stopped
  ("was started seeing GPUs '*' and may now see '-1'") and restarted on the CPU: **0 MiB** of VRAM.
- A save on the node's page lowering `Cores` to 2 → affinity **3** on the running server, no restart; the
  hub saw the new ceiling. `cpuPercent: 500` → 400 before anything was written.
- Killing the node outright took the `llama-server` with it: the nested job did not break 95 D3.

**Found there and fixed before the tag:** the Vulkan build of llama.cpp ignores `CUDA_VISIBLE_DEVICES`, so a
GPU limit set only that variable would have been a limit on paper. The node now sets `GGML_VK_VISIBLE_DEVICES`
(and the ROCm pair) as well. With no GPU allowed, llama.cpp logs "Invalid device index … in
GGML_VK_VISIBLE_DEVICES" for the `-1` and then runs on the CPU, as intended.

Tests: `EngineLimitTests` (Node — the clamp as a table of hostile and valid profiles, and the limiter against a
scripted container and readings), `EngineLimitProcessTests` (Mesh — a real Job Object refuses a 400 MB
allocation under a 150 MB limit and allows it under 2 GB, and holds a busy loop under 1 % of the machine; on
Linux, affinity pins every thread and the watchdog ends a process over its memory — both ran in CI on
ubuntu), `EngineLimitMeshTests` (a real hub and node over SignalR: the hub sees the ceiling, its lower limit
is in force, its API and a raw profile both fail to raise the node's), and `ConsoleContractTests` extended to
every field the new panel reads. `dotnet test InferHub.sln`: 2 094 passed, 63 skipped.

## Not established

- **A Linux cgroup on a box that delegates one.** The cgroup path (moving the node into a leaf, enabling
  `cpu`/`memory`, writing `cpu.max`/`memory.max`) has not run anywhere it could succeed; CI's runner and a
  default container cannot write `/sys/fs/cgroup`, and there the node correctly falls back and says so.
- **VRAM inside a container.** NVML may report the host's pids, which match nothing the node started; the
  runtime's VRAM then reads 0 and its limit cannot trip. Not tried on a GPU container.
- **Ollama and Strata were not run under limits.** The GPU was full with another program's model, so the
  live check used llama.cpp; the Ollama hook is only on the path where the node itself spawns `ollama serve`,
  and Strata's goes through the same `EngineProcess` as llama.cpp.
- **A runtime that starts children within microseconds of starting** could have one escape the Windows job
  (a process is put in the job right after `Process.Start`); the runtimes here start theirs much later.
- **The VRAM watchdog stops a runtime that keeps going over again and again**, each time after its
  supervisor's backoff — visible as a rising breach count, not prevented.

## Addendum — the published binaries, on the same box

`inferhub-node:3.67.0` and `inferhub-coordinator:3.67.0` pulled from GHCR (layers read through the registry
API, `app/` extracted; both carry revision label `66f402c`, the tagged commit) and run natively on Windows —
**binaries, not a container**, so this says nothing about the images' OS layer. The node launched the same
Vulkan `llama-server` with the same ceiling (`CpuPercent 10, Cores 4, MemoryMiB 4096`):

- All three reported `os (Job Object …)`; Windows showed the server's affinity as **15** (four cores).
- Hub `cpuPercent: 50` → **409** naming the node's 10%.
- Hub `vramMiB: 100` → the counters read **398 MiB** and the server was stopped, twice, each after its
  supervisor's backoff; the breach count and sentence reached `/api/status`.
- Hub `vramMiB: 2000` → 398 MiB, nothing stopped; then `gpus: []` → restarted, **0 MiB**.
- *Clear* → the node's own limits; the server restarted once more, because the GPU list in force changed
  back from none to any — as designed, and worth knowing: clearing a GPU limit restarts the runtime too.
- **The console panel was opened** (headless Chrome against the published coordinator,
  `.claude/verification-v3.67.0-console.png`): the row shows in force `5% / 4 / 4096 MiB / 3000 MiB`, under
  each the node's ceiling and the hub's value, the usage, and the last stop as a sentence.

`InferHub-Node-Setup-3.67.0-win-x64.exe` and its `.sha256` were attached by `windows-installer.yml`; the setup
was not run for this release (it carries the same node).
