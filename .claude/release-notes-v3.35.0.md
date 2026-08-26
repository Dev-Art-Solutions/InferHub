# v3.35.0 — The node speaks all four dialects, meshed and solo

Phase 67, the seventh release in the v3.29–v3.36 provider track and the first one on the other side
of the cut. Since v3.29 a **coordinator** can name four vendors, give each its own credential, reach
each through its own dialect and route to them by policy. A **node** could reach two of them: its
`Backend:Type` was `ollama` or `openai`, so `AnthropicUpstreamClient` and `GeminiUpstreamClient` —
both already in `InferHub.Shared`, both already driven by the hub — were unreachable from the box
that has the GPU. A GPU-less machine somebody wanted to run as *a node backed by Claude* could not
be configured at all.

`Backend:Type` is now `ollama` · `openai` · `openrouter` · `anthropic` · `gemini`. The same code the
hub drives, composed once more on the node — **not a second implementation**, which is what the
track's D8 asked for. Meshed, such a node registers, reports the models its allowlist names, and
answers `InferenceJob`s carrying Ollama JSON exactly as an Ollama node does; the hub cannot tell.
Solo, it is a private, authenticated, RAG-capable OpenAI-compatible front end to a vendor, in one
`docker run`.

**A node that changes no config behaves identically to v3.34** — same backend name, same endpoint,
same reported models, same declared capabilities, empty allowlist included.

## One node is one upstream, and that is the design rather than a limitation

**Considered and rejected: giving the node the hub's `Providers:` map.** It reads as symmetry and it
is a second router, on the host that has never had one: two policy vocabularies, two steer headers,
two places a prompt's destination is decided, and a node that can disagree with the hub that
dispatched to it. The asymmetry is the point — *the hub chooses, the node serves*. A deployment that
wants two vendors configures them on the coordinator, where the router has lived since v3.33, or
runs two nodes.

There is likewise **no `ModelMap` on the node**. The hub has one because it routes; a node reports
what it serves, so the allowlist is the whole consent and the vendor's own id is the model name.

## One class per seam, not per vendor

`OpenAiBackend` became `UpstreamBackend`, driving an `IUpstreamDialect`. That interface was extracted
in v3.29 and this is what it was extracted for: the node's second backend has always been *Ollama
JSON in, Ollama JSON out, over somebody else's HTTP* (22 D1), which is that interface's five members
exactly. A fourth vendor costs two arms in two switches — one for the dialect, one for the
credential, **because the credential is part of the dialect**: a Bearer token sent to Anthropic or
Gemini is a 401 that reads like a bad key.

Rejected: three more implementations of `IInferenceBackend`, each a copy of the same 120 lines.
Rejected: keeping the old name for a class that drives Anthropic.

## `Upstream:` is the section; `OpenAi:` still binds

Both bind to one options object with `Upstream:` layered second, so every node written since v2.4 is
untouched. **A key written in both sections with different values fails startup naming both** —
which upstream receives a prompt is not decided by which section a binder applied last. That is
v3.33's rule about `Policy`/`Trigger`, one host over.

What the new name buys is that the vendor keys can exist at all: `Upstream:MaxTokens`,
`Upstream:AnthropicVersion`, `Upstream:ThinkingBudget`, `Upstream:Referer` / `Upstream:Title` — the
same names `ProviderDefinition` already carries on the hub, so an operator moving a vendor from the
coordinator to a node retypes nothing. `OpenAi:AnthropicVersion` is the kind of key somebody
screenshots.

The three vendor base URLs now live in **one** place, `InferHub.Shared/Upstream/UpstreamDefaults.cs`,
read by both hosts. A URL written down twice is a URL corrected once.

## Anthropic declares `chat` and not `embed`, and that is the capability seam paying off a fourth time

`IInferenceBackend` gained `Kinds`, and `BackendCapabilities` takes it instead of the constant
`[chat, embed]` it used to hold. This is `SupportsModelManagement`'s own argument one member down —
*a backend that throws when asked to do the impossible is a seam nobody trusts twice*.

The payoff is v3.8's router: an embedding request against an Anthropic-backed fleet is a **503
naming `embed`** at the hub, *before* the hop, instead of a 501 inside a failed job. In solo mode
`/api/embed`, `/api/embeddings` and `/v1/embeddings` answer **501 naming the reason**. That refusal
is deliberately not merged with the `Node:Capabilities:Disabled` one: an operator's subtraction is a
503 worth retrying, and "this upstream has no such API" never will be.

v3.31's notes said the capability declaration was this phase's job. It was.

## A vendor-typed node with no allowlist refuses to boot

OpenRouter lists 419 model ids and Gemini around fifty, embed-only and image ones among them. A node
that reported the catalogue would be telling the coordinator it can chat with an image model, and the
router would believe it. So `openrouter`, `anthropic` and `gemini` require `Upstream:Models:Include`
or `Node:Models:Include` — either is the operator's sentence — and the startup failure names the key.

**`openai` is deliberately not held to this.** It is usually one vLLM serving one model, and every
such deployment since v2.4 has an empty allowlist; breaking them to protect them from a catalogue
they do not have is not a trade.

Rejected: a default allowlist per vendor. A model list checked into a repository is wrong by the time
it ships — the same reason this track refused a price table.

## The node's context file was at 1099 of 1100

So the tool-runtime phases moved whole into `src/InferHub.Node/Tools/CLAUDE.md`: **41, 42, 48, 55, 56
and 57/58**, the largest coherent subtree the provider track had nothing to do with, moved unedited.
1099 → 699, with the new file at 500. This is v3.30's split of the coordinator's file, one project
over, for the same arithmetic and with the same net under it —
`EveryPhaseDecisionBlockSurvivesTheSplitExactlyOnce` asserts every block still exists exactly once.

Rejected: compressing phase 41. Rejected: raising the budget — a limit raised on first contact is
not a limit.

## Zero new dependencies, for the sixty-seventh time

No new `PackageReference`. `InferHub.Shared.csproj` is still an empty `<Project Sdk="…">`. The three
dialects were already there; this release only composed them somewhere else.

## Tests

`dotnet test InferHub.sln` — **1 473 passed / 48 skipped**, run as CI does. The slice this phase
owns, `tests/InferHub.Tests.Node`, is 179 passed / 3 skipped and carries fifteen new cases: a
blocking and a streaming chat per new dialect against a stub, the credential header per vendor with
**no `Authorization`** where there should be none, the resolved default base URL per type, `Kinds`
per type, the allowlist narrowing a catalogue, and — in `NodeCompositionTests` — the legacy section
still binding, the conflict failing startup naming both paths, the missing-allowlist refusal, and
`openai` with no allowlist still booting.

## What was NOT established, said out loud

- **No live vendor endpoint was called by anything in this release.** Every dialect assertion here is
  against a stub or a payload recorded from the vendors' documentation when v3.31 and v3.32 were
  written — the track's D6, which is why a test that needs somebody's API key does not exist in this
  repository. **A real key against a real node is phase 68**, and until then "the node speaks
  Anthropic" is a claim about translation, not about a conversation anybody has had.
- **Nothing here has been driven on a published image yet.** The release checklist's fourth item is
  outstanding at the time of writing: pull `ghcr.io/dev-art-solutions/inferhub-node:3.35.0`, set
  `Backend__Type=anthropic` with a stub upstream on the host, and confirm the wire — `x-api-key`
  present, `Authorization` absent, `max_tokens` supplied — plus a solo `/api/embed` answering 501.
  Five of the last seven releases found something only the artefact could show.
- **The `Upstream:`/`OpenAi:` conflict check compares literal configuration values**, so two spellings
  of the same thing (`http://host:8000/v1` and `http://host:8000/v1/`) are reported as a conflict.
  That is the safe direction and it is not free of false positives; nothing measured how often it
  fires in practice, because nothing has run it outside the suite.
- **No measurement of what a provider-backed node costs the fleet.** A node whose "GPU" is somebody
  else's datacentre still occupies a `MaxConcurrency` slot and is still counted by the saturation
  check as though it were local. Whether that is the right accounting is a question this phase did
  not ask.
