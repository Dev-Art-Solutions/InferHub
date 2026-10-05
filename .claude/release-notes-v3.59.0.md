# InferHub v3.59.0 — a closed question answered with a distribution, not a sentence

v3.58 made [colibri](https://github.com/JustVugg/colibri) a node backend. colibri has a second way to
ask a model something, called **Brio**, and v3.58 left it out on purpose because it needs its own
API. v3.59 adds that API.

In Brio mode the model does not generate text. The engine reads the log-probability of each answer
you allow and normalises over those answers only. You get back every option's probability and an
entropy. So "the model does not know" shows up as a number instead of a confident sentence.

## What's new

### `POST /v1/brio`, on the hub and on a solo colibri node

The route is colibri's own, and the request body is passed to the engine unchanged:

```http
POST /v1/brio
{"model": "olmoe",
 "state": "The pull request removes the retry loop around the file upload and adds no test.",
 "question": "What should the reviewer do?",
 "options": ["merge", "request changes", "close"]}
```

A real answer from OLMoE int8 on a CPU, through a hub and a colibri node:

```jsonc
{"object": "brio.choice", "answer": "request changes", "entropy": 0.394847,
 "choices": [{"option": "request changes", "p": 0.8817}, {"option": "merge", "p": 0.0851},
             {"option": "close", "p": 0.0332}],
 "usage": {"prompt_tokens": 32, "completion_tokens": 0, "read_tokens": 4, "total_tokens": 36}}
```

All three of colibri's forms work. Each request uses exactly one:

- **`options`**: one question.
- **`questions`**: several questions about one `state`. The engine reads the state once.
- **`schema`**: `{"field": ["allowed", "values"]}`. The engine fills a JSON object one field at a
  time. The JSON cannot be malformed, because the model never writes it.

### How it fits into the fleet

- **A new capability, `score`.** A colibri node now declares `chat` and `score`. No other backend
  declares `score`. If no node can take a Brio request, the hub answers **503 naming `score`**. An
  unknown model is still a 404. `Node:Capabilities:Disabled: ["score"]` turns it off on one node.
- **It travels as a tool job.** Brio has no Ollama shape, so it is not squeezed into the inference
  job. It uses the tool-job contract the speech and image work already use: the same dispatcher,
  failover and deadlines (`Dispatcher:Deadlines:score`). `/api/tools/score` also reaches it.
- **Metered in tokens.** Brio generates nothing, so the usage row (kind `score`) records everything
  the engine read as prompt tokens: the shared prefix plus every option token. It counts against
  the same token quota as chat, because it is the same engine. Refused and failed requests are not
  billed. A 200 with no `usage` block is treated as a 502, not as a free answer.
- **The engine's errors keep their meaning.** Its 400s come back as 400 in its own words. A full
  queue (429) becomes a 503 with `Retry-After`; the node does not retry it. An unknown model
  becomes a 404.
- **KV slots.** The node adds nothing to the body. colibri pins a Brio request to a KV slot by
  hashing its `state` itself, so many questions about one document land on one slot.
- **Solo mode** takes a concurrency slot for a Brio request, the same as for a chat. It is the same
  engine.
- **Privacy.** Neither host logs the `state`, a question or an option. The log line has the model,
  the form, the number of options and the token count.

## Checked against a real engine

colibri v1.12.1 (`coli serve`, OLMoE int8, the engine from the published `3.58.0-colibri` image) ran
in a container. A coordinator, a meshed node and a solo node ran from source and connected to it.

| What | Result |
|---|---|
| Node registration | `Reported 1 of 1 models from colibri backend as chat, score` |
| `options` via hub | 200, `request changes` p=0.88, 36 tokens, 1.3 s (the engine was already warm) |
| `questions` (3 questions) via hub | 200 in 11.7 s, 77 tokens: `no` (satisfied?), `a reprint` p=0.99, `production` (entropy 0.54) |
| `schema` (3 fields) via hub | 200 in 2.8 s, 64 tokens: `{"sentiment":"negative","intent":"refund","urgency":"high"}` |
| Same `options` on the solo node | 200, `X-InferHub-Served-By: node-solo`, identical probabilities to the hub |
| One option (engine refusal) | 400, the engine's sentence: *"`options` needs at least two options to choose between."* |
| Unknown model | hub 404 (the router knows it before the hop); solo 404 with the engine's sentence |
| No form | 400 at the edge; the engine was not called |
| `/api/tools/score` | 200, and **not billed** (true of every generic tool call) |
| Ledger | three `/v1/brio` successes, 36 + 77 + 64 = **177 prompt tokens, 0 completion** |
| Logs on all three hosts | none of the state, question or option phrases appear |

The first direct call to a cold engine took 30 s. Later calls took 1–12 s on a 32-core CPU.

Running it against the real engine found **no bug in the product**. The new mesh test did find one
in its own harness: a fake gateway written as `Task<IResult> Handler(HttpContext)` binds as a plain
`RequestDelegate`, and its result was dropped for an empty 200. The renderer refused that empty 200
as "not a Brio answer". That refusal is the reason the guard exists.

## The published image

`inferhub-node:3.59.0-colibri` pulled from GHCR. Its revision label is `32e80c9`, the commit the
`v3.59.0` tag points at. It ran as a solo node with OLMoE mounted and nothing else configured, so
the node launched `coli serve` itself:

| What | Result |
|---|---|
| `/api/status` | `"nodeVersion":"3.59.0"`, `"capabilities":["chat","score"]`, 5 s after `docker run` |
| `options` | 200 in 26 s (cold engine), `request changes` p=0.8817 / 0.0851 / 0.0332, 36 tokens: the same numbers as before release |
| `questions` | 200 in 10.6 s, 77 tokens, the same three answers |
| `schema` | 200 in 2.5 s, `{"sentiment":"negative","intent":"refund","urgency":"high"}`, 64 tokens |
| No API key | 401 |
| Duplicate option | 400, the engine's *"Duplicate option in `options`: 'a'."* |
| Container log | none of the state, question or option phrases appear |

All seven images (`coordinator`, and `node` plain, `-tools`, `-ollama`, `-diffusion`, `-all`,
`-tts-bg`, `-colibri`) are published.

## Found after the tag

The `build-and-test` run on the release commit failed once, on
`DispatchDeadlineTests.AStreamThatNeverStartsDiesAtItsCapabilitysDeadline`. That is a phase-89 test,
and this release did not touch that code. When a stream ran out its deadline, the hub woke the
waiting caller *before* it sent the node `CancelJob`. So a caller, or a test, could see the timeout
before the node had been told to stop. The blocking path already did it in the right order. The two
stream paths now tell the node first. Nothing a client can see changed, and the fix is on `main`.
There is no v3.59.1.

## What was not established

- **Brio across more than one KV slot.** OLMoE accepts one slot. Only GLM-5.2/5.3 accept more
  (372 GB+, which we cannot run). The engine's own state-hash pinning is described from its source,
  not measured.
- **The "state read once" saving.** colibri reports 5.7× for the `questions` form. We did not measure
  it against separate `options` requests.
- **The 503 for a fleet with no scorer on a live stack.** That path is covered by the mesh test (a
  real hub, SignalR and node, with `score` disabled), not by a live run.
- **How good OLMoE's judgements are.** The numbers above are what the engine said. This release is
  about how the answers travel through the fleet, not whether they are right.

## Compatibility

A deployment that changes no config behaves the same. A colibri node declares one more capability
kind, and a hub older than v3.59 carries that kind without routing it. Zero new `PackageReference`;
`InferHub.Shared.csproj` is still empty. Tests: 1 803 passed, 61 skipped.

## Images

This release changes the coordinator and every node image (the node code is the same across them).
The one that exercises this phase is `inferhub-node:3.59.0-colibri`.
