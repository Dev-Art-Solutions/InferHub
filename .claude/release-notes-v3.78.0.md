# InferHub v3.78.0 — models kept for the owner's machine

Phase 117, plus InferHub Desktop 0.3.0 on its own tag. With 3.76 and 3.77 a node's owner decides *whether* and
*when* the network may use the node. The question that follows in a network of friends' PCs is *what*: "the 32B
model is for my own work, the small one may serve anybody". v3.78 adds that as one live setting on the node:

```json
"Node": { "Sharing": { "Models": { "Exclude": ["qwen3:32b"] } } }
```

## What it does

- **`Exclude`** shares every model except these, and **`Include`** shares only these. Names are matched the way
  `Node:Models` matches them, ignoring case. When both lists are empty (the default), the network may use every
  model, as in 3.77. A name in both lists stops the node from starting, and the control API refuses it.
- **A kept model is absent, not paused.** The node does not report it to the hub. No coordinator, of any version,
  routes to it, lists it in `/api/tags`, or names it under a capability. A request for it at the hub is a `404`
  naming the model, the same as for a model no node has. No hub change is needed, and nothing new goes over the
  wire.
- **It is still the owner's.** The local API lists and serves a kept model, and the control API and the desktop app
  show it. `Node:Models` still decides what the node serves at all. `Node:Sharing:Models` only narrows what the
  network hears about.
- **A change reaches the hub at once.** The node reports its models again as soon as the list changes, without
  waiting for the model refresh. A network job that is already running on a model that has just been kept
  finishes. The same rule as 3.76's pause applies.

## The control API

- `PUT /api/control/sharing {"models": {"include": [...], "exclude": [...]}}`. A list that is left out keeps its
  value, `[]` clears that list, and `{}` is a 400.
- `GET /api/control/models` and `GET /api/control/status` report `"shared": true|false` for each model. The
  field is left out for a model that `Node:Models` leaves out, since the node serves that one to nobody.
  `status.sharing.models` shows both lists, and `/models` carries them under `sharing`.

## InferHub Desktop 0.3.0

The Models tab has a **Shared** box beside each model, and the Overview says what is kept ("Kept for this machine:
qwen3:32b — the network may use 2 of 3 models"). If the owner wrote an `Include` list, the box adds names to it
and removes them. Unticking the last name is refused, because an empty `Include` means every model. Otherwise
the box puts the model on `Exclude` or takes it off, so a model pulled later is still shared, as before. The box
appears only when the node is 3.78 or later.

## Upgrading

Nothing changes until a list is set. A 3.77 hub works with a 3.78 node as it is: the node simply reports fewer
models.

## Not covered

- A hub profile can still pin a kept colibri or Strata catalogue model. The model loads on the node, but the
  network still cannot route to it.
- Tool and image recipes are not models. `Tools:Allowed` and hub profiles decide those, as before.

## Verification

`dotnet test InferHub.sln`: **2 433 passed, 63 skipped**. A mesh test runs a real hub and a real node over SignalR:
a kept model is absent from the hub's registry, even with unserviceable nodes counted. It comes back when shared,
and an `Include` list takes the other model away. With the filter removed, that test goes red.

**A live run from source**, with a 3.78 hub and node and the real Ollama on this machine (29 models, `Node:Models`
narrowed to `qwen2.5:0.5b`, `gemma:2b`, `nomic-embed-text`):

| Step | Result |
|---|---|
| Before any list | hub `/api/tags`: all three |
| `PUT sharing {"models":{"exclude":["gemma:2b","nomic-embed-text:latest"]}}` | hub `/api/tags` shows only `qwen2.5:0.5b` **0.97 s** later. The node logs "Reported 1 of 29 models … 2 kept for this machine" |
| Hub `POST /api/chat` with `gemma:2b` | `404 model 'gemma:2b' not found` |
| Hub `POST /api/embed` with `nomic-embed-text` | `404 no node is advertising embedding model …` |
| Hub chat with `qwen2.5:0.5b` | 200, answered |
| **Local** API chat with `gemma:2b`, embed with `nomic-embed-text` | both 200: the owner's own use is untouched |
| Node restarted | the list was still in `node.local.json`, and the hub again saw only `qwen2.5:0.5b` |
| `gemma:2b` given back | on the hub **0.85 s** later, and a hub chat with it answered |
| Desktop 0.3.0 (from source) attached to that node | Shared boxes on exactly the three served models, and the Overview reads "Kept for this machine: nomic-embed-text:latest — the network may use 2 of 3 models" |

**What the run corrected before release:**

- Every model that `Node:Models` leaves out (26 of them) was reported as `"shared": true`. The node serves those
  to nobody, so the field is now left out for them, and the app shows no box.
- The first draft of these notes said that keeping the only embedding model withdraws `embed` from the node.
  That is false for Ollama, which declares its kinds for the whole engine. The node still declared `chat, embed`
  over `qwen2.5:0.5b`. What holds is narrower: the kept model is named under no capability, and the hub refuses
  it with a 404.

**Not established:** a network job that was already running on a model when that model was kept (it should finish,
as with 113 D3), and clicking the Shared box in the app. The request the box sends was sent with `curl`, and the
toggle logic is unit-tested.
