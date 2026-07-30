# InferHub v3.8.0 — a node that says what it can do

Until this release a node advertised **a list of model names**, and the coordinator routed on that
and nothing else. It worked for three years of releases because of an assumption nobody wrote down:
that every model on a node does the same kind of work.

It does not. A box holding only `nomic-embed-text` was a perfectly good candidate for a chat request
naming that model — the router had no way to know otherwise — and the error came back from the
backend, after a dispatch, seconds later. That is the small version of the problem. The large
version is the one this release exists for: a node that runs a speech model, or anything else that
is not a language model, has no way to say so.

So the unit of routing is now the pair **`(capability, model)`**.

```bash
curl -s localhost:5080/api/status | jq .capabilities
# [ { "capability": "chat",  "nodes": 2, "models": ["llama3.2", "qwen2.5"] },
#   { "capability": "embed", "nodes": 3, "models": ["llama3.2", "nomic-embed-text", "qwen2.5"] } ]

curl -s localhost:5080/v1/models | jq '.data[] | {id, capabilities}'
# { "id": "nomic-embed-text", "capabilities": ["embed"] }
```

## Nothing is guessed

Ollama does not say what a model is for. Guessing from the name — "it has *embed* in it" — would be
a lookup table that is built, believed, and wrong for somebody, which is a mistake this project
already declined to make once (there is no registry of which models accept images either; the model
refuses and we forward the refusal).

So a node declares `chat` + `embed` over everything its backend reports, and the one thing it cannot
work out for itself is one line of configuration:

```jsonc
"Node": { "Capabilities": { "Disabled": ["chat"] } }   // this box is for embeddings
```

The key is **subtractive only**. You can narrow what a node is used for; you cannot make it claim
something it has not got. Disabling both `chat` and `embed` fails startup — a node routed for
nothing is a machine burning power for nothing.

## A capability nobody has is a 503, not a 404

If the model is on the fleet but no node will do *this* with it, the answer is
`503` + `Retry-After`, naming the capability:

```
no node currently provides 'chat' for model 'nomic-embed-text'
```

That is deliberately the same shape as "every node is busy", because it is the same kind of fact: a
statement about the fleet right now, not about what exists. A model that genuinely is not on the
fleet is still the `404` it has always been, byte for byte. And the check runs *after* admission, so
it can never be used to find out which models live behind a scope you were not granted.

## Solo mode enforces the same key

`Node:Capabilities:Disabled` means the same thing on a standalone node — the same `503`, the same
`Retry-After`, in both dialects. Only the enforcer moves, from a router that is not there to the
node that is. A key that is honoured in one deployment and silently ignored in another would be
worse than the asymmetry. (Its own corpus is exempt: solo RAG still embeds with `embed` disabled,
because a node's own documents are not somebody sending it work.)

## Upgrading

**Nothing to do.** A node that declares no capabilities — every node before v3.8, and every v3.8
node whose operator has not touched the new key — is read as chat + embed over everything it
reports, which is exactly the old behaviour. A **v3.7 node against a v3.8 coordinator registers and
serves normally**, so a fleet can be upgraded one box at a time. The test that pins this is the
first one in the new suite, and it is about the old behaviour rather than the new.

Additive elsewhere: `/api/status` grows a `capabilities` block and a per-node list, `/v1/models`
grows a `capabilities` field (omitted entirely for a model nothing can serve, rather than sent
empty), and the console and status page grow a column.

**Zero new dependencies**, `InferHub.Shared.csproj` is still empty, and no `.csproj` changed.

## What this is for

This is the first release of a track. Routing has to learn that "which model" and "what kind of
work" are two different questions before a node can run anything that is not a language model. Next:
a tool runtime that lets a node drive a supervised subprocess — Python, in practice, because that is
where the libraries are — and then speech-to-text and text-to-speech behind the OpenAI audio API.
