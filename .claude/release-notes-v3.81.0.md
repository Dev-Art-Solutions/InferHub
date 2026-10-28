# InferHub v3.81.0 — models from the coordinator, on the owner's terms

Phase 120. v3.79 and v3.80 gave a node's owner **Check fit** and **Browse models…** in the desktop app. A node on a
far-away VM has no owner sitting at it. v3.81 lets the node's coordinator do the same thing, but only where the owner
said yes at install, and only within the disk limits the owner set.

```http
GET  /api/admin/nodes/{id}/fit/catalog?source=all&sort=trending&kind=text&limit=30
POST /api/admin/nodes/{id}/fit          {"link": "Qwen/Qwen2.5-0.5B-Instruct-GGUF", "context": 16384}
POST /api/admin/nodes/{id}/fit/install  {"link": "…", "context": 16384, "engine": "llamacpp", "variant": "q4_k_m"}
```

## What it does

- **The hub asks; the node answers.** Each call goes down the connection the node opened, to that node only. Each
  answer is the node's own `/api/control/fit…` reply, passed through unchanged. The hub keeps no second copy of the
  estimate.
- **Off until the owner says yes.** `Node:Management:AllowFromHub` is `false` by default, and the Windows setup asks.
  While it is off, all three calls get a 403 with the node's reason, and nothing reaches the network. The node reads
  the key on every request, so a change in `node.settings.json`, `node.local.json` or the app's settings applies to
  the next request, with no restart.
- **The owner's disk terms.** An install from the hub can be no larger than `Node:Management:MaxModelGiB` on disk (no
  cap by default). It must also leave `Node:Management:KeepFreeGiB` free (20 by default) on the disk where that
  engine keeps its models. If either limit would be broken, the install is refused with a 409 that gives the numbers.
  If the node cannot measure that disk, it refuses the install while a floor is set. The estimate's reply has `terms`
  and `hub[]`, which says for each option whether the hub may install it, so the console shows those options as
  unavailable before anyone clicks.
- **The install is the pull you already know.** The node checks the chosen option and the context against its own
  estimate and its terms, then names the pull. The hub sends that pull as a model command, so its progress shows under
  *Command progress* like any other. The context goes where each engine keeps it (118).
- **The console:** *Model management → Models that fit*. Pick the node, then **Browse** (source, order, text or
  vision, a search, fits only) or **Check fit** (a link and a context), then **Install**. A line shows the node's
  terms. `/api/status` and `/api/admin/nodes` carry `nodes[].management`, the terms the node had when it connected.
- **The setup** (Windows) has two new pages after *Updates*: *Models from the coordinator* (yes or no, default no),
  then the two numbers. A solo node is never asked. For a silent install:
  `/HubModels=yes /HubMaxModelGiB=60 /HubKeepFreeGiB=50`.

## Found live, fixed before release

- **The console showed every node as "older than 3.81."** It reads its node list from `/api/admin/nodes`, not from
  `/api/status`, and that list did not carry the terms. The tests passed, because they call the API, not the page.
  Opening the console in a real browser found it.
- **A message gone in a few seconds.** "Installing…" and the refusal sentences went to the model-management note,
  which the node-list refresh rewrites every few seconds. The panel now has its own note line.

## Upgrading

Nothing changes until a node's owner sets `Node:Management:AllowFromHub=true`. The older hub commands are not covered
by the new keys: pull by name, a Hugging Face link and a Strata install behave as in v3.80. `HuggingFace:Enabled` still
gates every Hugging Face download. A node before 3.81 is a 409 from the hub, without a call to it.

## Not established

- **A hub-side refresh of the terms after a live change.** The console shows the terms the node had when it
  connected. The node's own answers are always current.
- **A node on another machine.** The live check ran the hub and the node on one box, over a real SignalR connection.
- **The setup's two pages, clicked by hand.** The script compiles, and the keys it writes go through the same
  `configure` path as every other answer.

## Verification

`dotnet test InferHub.sln`: **2 577 passed, 63 skipped** (Desktop 36, Shared 433, Coordinator 896, Node 687, Mesh 525).
The first full run hit one known flake, `ToolUploadTests` (the node's upload limit, under load). It passed when run on
its own, and the second full run, after the console fixes, was clean. New tests:

- `HubManagementTests`: off by default, and nothing is asked; the key is read live; the estimate includes the terms
  and a verdict per option; the two disk limits as a table; the estimate's own refusals keep their status.
- `ManagementMeshTests`: over a real hub and node, the 403, then the estimate after the owner allows it, then the
  install. The GGUF lands in the store with the chosen context. An install over the floor gets a 409.

Live on the 3090 Ti box (256 GB), hub and node built from source in mesh mode, with a real Ollama, the winget Vulkan
llama.cpp and real Hugging Face:

| Through the hub | Result |
|---|---|
| `fit/catalog?source=all&limit=12` | both sources answered in 4 s |
| `fit` for `Qwen/Qwen2.5-0.5B-Instruct-GGUF` at 16K | 8 options, all allowed under 30 GiB / 20 GiB free |
| `fit/install` llamacpp `q4_k_m` at 16K | 202. The first download was cut by the CDN (connection reset); the second resumed and landed with `Context: 16384` in its marker. The router served it with `n_ctx` 16384, and a chat through the hub said "Paris" in 2.3 s |
| `fit` / `fit/install` for `llama3.1:70b` | Ollama's 39.6 GiB option `allowed:false`; the install was a 409, "at most 30 GiB per model" |
| `AllowFromHub` set to false in `node.local.json`, then back | 403 with the node's reason, then 200, with no restart |
| the console in headless Chrome | terms line, 43 rows for `qwen` + fits only, Check fit at 32K with two **Install** buttons; Install for `qwen2.5:0.5b` sent the pull, and Ollama's context was stored as 4096 |
