# InferHub v2.4.0 — OpenAI-compatible node backend & cloud burst

Phase 22. Two features, one translation layer, mirrored — and **zero new dependencies**.

## A node is no longer synonymous with Ollama

`IInferenceBackend` has had exactly one implementation since phase 3. It now has two.

Set `Backend:Type=openai` and a node drives any server that speaks the OpenAI wire format:

| Upstream | Why you'd want it |
|---|---|
| **vLLM** | Continuous batching and paged attention — the difference between one GPU serving four users and one serving forty. |
| **llama.cpp server** | GGUF quants, CPU/Metal, tiny footprint. |
| **LM Studio** | A desktop model runner people already have open. |
| **TGI** | HuggingFace's inference server. |
| Hosted providers | OpenAI, or anything wearing the same dialect. |

```jsonc
{
  "Backend": { "Type": "openai" },
  "OpenAi": {
    "BaseUrl": "http://localhost:8000/v1",
    "Models": { "Include": ["meta-llama/Llama-3.1-8B-Instruct"] }
  }
}
```

One implementation covered all five, because they all converged on the same format — three
roadmap items ("vLLM backend", "llama.cpp backend", "hosted backend") collapsed into one, and
the collapse was possible only because v2.3's translation code could be run backwards.

Against a hosted provider, `OpenAi:Models:Include` is effectively mandatory: the catalogue is
hundreds of models the node cannot serve, and reporting them all makes the coordinator route
anything to it. `Digest` and `SizeBytes` come back `null`, because an OpenAI-compatible server
reports a name and nothing else and inventing values would be worse.

A node with `Backend:Type=openai` and no `BaseUrl` **refuses to start**, naming the key. A node
that boots and then 500s on every job it is handed is worse than one that doesn't boot.

## Cloud burst

The failure nobody plans for is that the GPU box is switched off. A coordinator with no capable
node has returned a flat `404` since 1.0. It can now fall back to a configured
OpenAI-compatible upstream instead — a hosted provider, a second mesh, anything speaking the
dialect.

This is the feature most likely to do something its owner never agreed to, so it is fenced in:

- **Off by default.** Upgrading changes nothing.
- **Mapped models only.** `Fallback:ModelMap` *is* the consent. There is no wildcard.
- **Always tagged.** Every fallback response carries `X-InferHub-Served-By: fallback`.
  Node-served responses carry `node`.
- **Always counted.** `/api/status` and the status page report cloud burst and its counter
  *even when it is off* — "is this thing sending my prompts anywhere?" should not require
  reading a config file.
- **Never stored.** The coordinator forwards in flight and streams straight through. It retains
  neither the prompt nor the answer.

`Trigger: no-node-or-saturated` also bursts when every node holding the model is at its
**declared** `MaxConcurrency`. A node that declared no cap is never saturated — there is no
number to compare against, and guessing would burst to a paid upstream on a hunch. If a
saturation burst fails and a node exists, the request goes to the node: bursting on saturation
is an optimisation, not a promise.

## The bit that looks silly, and why it stays

An OpenAI request to `/v1/chat/completions` is translated into an Ollama body, sent to a node,
and translated back into an OpenAI body for vLLM. Two `JsonSerializer` round-trips on a call
that is about to occupy a GPU for two seconds.

The alternative was a polymorphic job payload carrying a dialect tag, which would have pushed
dialect-awareness into the dispatcher, the router, the affinity keys, the retrieval pipeline,
and every test that touches them. The mesh's internal protocol stays exactly one shape. That is
worth more than the round-trips cost, and the reason is written into `UpstreamTranslator` so
nobody "fixes" it later.

## Fixed

- A node reported `Ollama:Endpoint` as its endpoint at registration **regardless of backend**,
  so an OpenAI-backed node would have advertised `localhost:11434` on the status page while
  driving vLLM. `IInferenceBackend` now carries `Endpoint`. The `NodeRegistration.OllamaEndpoint`
  field name is unchanged — it is a SignalR payload field and an `/api/status` field, and
  renaming it would break a mixed-version fleet for a cosmetic gain.

## Internal

- The OpenAI DTOs and shape mappers moved from `InferHub.Coordinator/OpenAi/` to
  `InferHub.Shared/OpenAi/`: both ends of the mesh speak the dialect now, and two copies of a
  wire format drift silently. Only the ASP.NET-bound pieces (`OpenAiEndpoints`,
  `OpenAiStreamingResult`) stayed behind. Pure refactor — **not one test file changed**.
- `OpenAiUpstreamClient` (in `InferHub.Shared`) is the single implementation of the upstream
  dialect: HTTP, SSE parsing, error mapping. The node's `OpenAiBackend` and the coordinator's
  `FallbackDispatcher` both drive it. One dialect, one implementation, two callers.
- **Zero new NuGet packages.** `HttpClient` and `System.Net.Http.Json` ship in the shared
  framework; the SSE *parser* is written by hand, exactly as the SSE *writer* was in v2.3.

## Tests

361 total, 347 passing, 14 skipped (the gated Postgres integration suite).
New: `OpenAiBackendTests` (17), `FallbackTests` (18) — most of the latter being assertions
about when cloud burst must **not** fire.
