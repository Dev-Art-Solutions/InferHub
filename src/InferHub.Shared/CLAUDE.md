# InferHub.Shared — agent context

**Scope: `src/InferHub.Shared/`.** Contracts, the OpenAI and Ollama dialects, the retrieval core,
the vector stores, and everything a client can observe about an image or an audio response.

> **Read the root `CLAUDE.md` first** — the seven design rules live there and they bind everything
> here. **Rule 2 and rule 5 are this project's whole character**: it is a plain class library with
> **zero** `PackageReference`, and the day it grows one is the day "the shared library is free"
> stops being true. If you are about to add a package here, you are about to be wrong.

## Why so much lives here

Three phases moved code *into* this project rather than copying it, and the reasoning is the same
each time: a decision that exists twice is a decision that diverges, and retrieval, the OpenAI
dialect and the image envelope each have a dozen of them whose divergence produces *plausible*
output rather than an error.

- **Phase 22** — the OpenAI DTOs, because the node speaks the dialect upstream too.
- **Phase 38** — the whole retrieval core, so a solo node runs the same pipelines as a hub.
- **Phase 44** — the Qdrant store, free only because phase-33 D2 hand-rolled it with no dependency.

The seam that made each possible is the same one: `IVectorLog`, `IRetrievalMetrics` and plain
options objects, so nothing here needs `ILogger` or `IOptions<T>`.

## Related context

- The hosts that consume this: `src/InferHub.Coordinator/CLAUDE.md`, `src/InferHub.Node/CLAUDE.md`
- The mask convention this project *names* and the worker *applies*: `python/CLAUDE.md` (50 D2)
- The tool protocol whose frames live here, driven from the node: `src/InferHub.Node/CLAUDE.md` (41 D1)

## Decisions recorded here

### Phase 24 (hybrid search, reranking, eval harness) — also load-bearing

**D1 — Keyword search is provider-native; zero new dependencies.** Under `postgres` it is a
`tsvector` generated column (`to_tsvector('english', payload text)`) with a GIN index and
`ts_rank_cd` — Postgres full-text search, which `Npgsql` already reaches. Under `local` it is
[InvertedIndex](src/InferHub.Shared/Vector/InvertedIndex.cs), a BM25 (`k1=1.2, b=0.75`)
dictionary that sits beside the `FlatIndex` in every collection. **`InvertedIndex` is derived, never
authoritative** — rebuilt from the raw store on startup and updated on every upsert/delete under the
same collection write-lock as the `FlatIndex`, exactly so the two can never diverge. Pulling in
Lucene to rank a few thousand chunks would be the wrong trade. Rule 5 survived again: `SearchKeywordAsync`
is the seam, both providers implement it, and nothing new was added.

**D2 — Fusion is Reciprocal Rank Fusion, not score blending.**
[HybridSearch](src/InferHub.Shared/Vector/HybridSearch.cs) fuses the two result lists by *rank*
(`Σ 1/(60+rank)`). Vector distances and BM25 scores live on different scales that no fixed constant
reconciles across corpora; normalising them is a corpus-specific guess dressed up as sophistication.
The fused RRF score replaces the branch's native score on the returned `VectorMatch`.

**D3 — The keyword branch is always hub-local, and that is recorded, not silent.** Node replicas
(phase 15) serve *vector* reads only. So in `hybrid` mode the vector branch may be served from a node
replica while the keyword branch runs against the hub's `InvertedIndex` — the pipeline logs this
rather than quietly dropping to vector-only. It never returns vector-only results while claiming to be
hybrid.

**D4 — Reranking reuses a fleet model; every failure keeps the original order.**
[LlmReranker](src/InferHub.Coordinator/Vector/LlmReranker.cs) (behind
[IReranker](src/InferHub.Shared/Vector/IReranker.cs), the one implementation) hands the top
candidates to a chat model already on the fleet with a scoring prompt and reorders by the parsed
scores. No node, a timeout (`RerankTimeoutSeconds`), an unparseable answer, a wrong-length score
array — **all return the candidates untouched**. A reranker that can break retrieval is worse than
none. Rule 7 holds: the query and candidate text pass through in flight, nothing is retained. A
dedicated cross-encoder (Cohere/Jina/TEI) fits behind `IReranker` later — the seam is built, with one
implementation.

**D5 — Retrieval mode and rerank are per-request, defaulting to pre-v2.6 behaviour.**
`X-InferHub-Retrieve-Mode: vector|keyword|hybrid` and `X-InferHub-Rerank: true`; unknown values are a
`400`, not a silent fallback. `Retrieval:Mode` defaults to `vector` and `Retrieval:Rerank` to `none`,
so a deployment that sends no headers and changes no config behaves **byte-identically to v2.5** — a
feature that silently changes existing results is a regression wearing a feature's clothes.
`RetrievalPipelineTests` asserts the default equals vector-only, and the exact-term case (an error
code) that vector search misses and hybrid recovers.

**The eval harness ships with the feature.** [tools/InferHub.Eval](tools/InferHub.Eval) is a standalone
console tool (no project references, **not** built into the images) that runs a golden set against a
live coordinator in every mode and reports Recall@k / MRR / nDCG@k / latency via the phase-24 search
endpoint `POST /api/collections/{c}/search`. "Hybrid improved retrieval" is an empirical claim; this is
how it is measured. Its README carries the load-bearing warning: a golden set generated by the model
you are about to evaluate is a mirror, not evidence.

### Phase 29 (vision passthrough) — also load-bearing

**D1 — Text and images are one array on one side and two fields on the other, so the
translator splits rather than joins.** `RequestTranslator.ExtractContent` returns a
`MessageContent(Text, Images)` pair instead of a joined string; `image_url` parts become
Ollama's base64 `images` array. `UpstreamTranslator` mirrors it for `Backend:Type=openai`
nodes. A message with no images emits **no** `images` key and goes upstream as a plain
`content` string — a stray empty array on every ordinary message would be a wire change for
requests that have nothing to do with vision.

**D2 — `data:` URLs only; the coordinator does not fetch remote images.** An `http(s)`
`image_url` is a `400` that says why. Fetching a caller-supplied URL makes the hub an SSRF
proxy and pulls third-party bytes through a hop designed to retain nothing (rule 7). Inlining
is one line in every OpenAI SDK, and it is the caller's job. Do not "helpfully" add a fetcher.

**D3 — Media type is sniffed from the magic bytes, never defaulted.** Ollama's `images` are
bare base64 with no type; an OpenAI data URL needs one. PNG/JPEG/GIF/WebP are recognised and
anything else is a clean `400`. Defaulting to `image/png` for bytes that are not a PNG turns a
detectable error into a bad model answer. The signatures are **not** `u8` literals — `0x89` and
`0xFF` are not ASCII, and a UTF-8 literal encodes them as two bytes each, a signature that
matches nothing. That bug was live for one test run; keep the explicit byte arrays.

**D4 — Base64 is validated at the edge.** A node rejecting malformed base64 seconds later, from
behind routing and a queue wait, is a much worse error than a `400` in the translator.

**D5 — No capability registry, deliberately.** A text-only model handed an image errors at the
node, and that refusal is forwarded as-is — a clean `502` carrying the model's own message,
never a `500`. Ollama is the source of truth for what a model accepts; a second list here of
"which models see images" would drift and start lying. *This is a recorded deviation from the
phase brief, which asked for a `400`/`404` shape:* the request was well-formed and it was the
upstream that refused, so `502` is the honest status. What the brief actually cared about — no
`500`s — holds.

**D6 — Node errors are unwrapped before they reach a client, and that is presentation, not
interpretation.** Found live in phase 29: Ollama encodes *its* backend's JSON error as a
**string** inside its own `error` field, so a llama.cpp refusal arrives double-encoded and our
envelope made three layers. A client read `error.message` and got
`{"error":"{\"error\":{\"code\":400,\"message\":\"…\"}}"}` — a wall of backslashes instead of
the one sentence saying what to fix, which is precisely the "useless unknown error" the OpenAI
envelope exists to prevent. `InferenceCore.ReadableNodeError` drills to the innermost message,
bounded at four levels, and lives in the **one** dispatch path so both dialects get it.

It unwraps; it never infers. Nothing is decided from the error *text* and the status code is
untouched — the moment this function starts deciding what an upstream error *means* (mapping it
to a 4xx, sniffing for "unsupported"), it has become the capability registry D5 refused, by the
back door. `NodeErrorReadabilityTests` pins the real captured Ollama payload.

**Rule 5 survived again.** Phase 29 added **zero** new dependencies.

### Phase 33 (Qdrant vector connector) — also load-bearing

**D1 — A third `IVectorStore`, and it is an *external* provider — so it is phase-20 §2 again.** Node
replication and self-healing are off; `NullVectorQueryRouter` stands in; the store publishes its own
`vector.collection.created` / `.dropped`. The one predicate every call site branches on is
`VectorStoreProviderExtensions.IsExternal` (postgres **or** qdrant), not `IsPostgres` — see "Vector
providers". `VectorCompositionTests.QdrantProviderRegistersQdrantStoreAndNoMeshServices` pins it.

**D2 — Zero dependency, by hand, on purpose.** See rule 5. `QdrantClient` is a hand-rolled REST
connector over `HttpClient`; the gRPC client was considered and declined. Do not add it.

**D3 — Qdrant point ids are UUIDs; the real id lives in the payload. This is the load-bearing
gotcha.** Qdrant accepts **only** an unsigned int or a UUID as a point id, and InferHub ids are
neither. So [QdrantIdMap](src/InferHub.Coordinator/Vector/Qdrant/QdrantIdMap.cs) maps each real id to
a deterministic `UUIDv5`, and the real id — with payload and metadata — rides in the point payload
under reserved `__*` keys (`__id`, `__payload`, `__meta`, `__seq`, `__ts`); reads unpack it, so
nothing above the store ever sees the UUID. **Determinism is what keeps re-ingest idempotent**
(phase 23 D5): the same real id addresses the same point and therefore *replaces*.
`QdrantIdMapTests` asserts determinism and no collisions over a large sample. A metadata filter maps
to a Qdrant `must` match on `__meta.<key>`, which excludes points lacking the key — the same
"null-metadata never matches" rule `FlatIndex` honours.

**D4 — Scan sorts client-side, because Qdrant scrolls by its own point id (a UUID), not by the
InferHub id the contract promises.** `ScanAsync` materialises the filtered set and orders by real id
in memory, then windows by `afterId` + `limit`. Correct, at the cost of reading the whole filtered
set per call — fine for the per-document scans that dominate ingestion (small sets), and the reason
`DeleteByFilterAsync` counts first (Qdrant's delete-by-filter returns no count, and the contract
must). Phase 35's migration adds a with-vectors scan; keep this ordering discipline there too.

**D5 — Keyword search is coarse under qdrant until v3.2, and honest about it.** Qdrant's full-text
index is a filter, not a ranking, so `SearchKeywordAsync` scrolls a **bounded** slice
(`KeywordScanCap`) and ranks by term-overlap in the chunk text — enough to give hybrid a real second
branch, explicitly not BM25. Phase 34 replaces it with server-side sparse-vector fusion. Records with
no text payload contribute nothing, the same stance `ChunkText` takes.

**Rule 5 survived again.** Phase 33 added **zero** new dependencies.

### Phase 34 (Qdrant-native hybrid search) — also load-bearing

**D1 — Under qdrant, hybrid fusion moves into the engine; the seam grows a capability, not a
method.** Qdrant's Query API fuses a dense and a sparse (lexical) vector server-side by RRF in one
round trip. That is a better hybrid than the hub fusing a real dense branch with the phase-33 coarse
keyword branch — so a new capability interface,
[IServerSideHybridSearch](src/InferHub.Shared/Vector/IServerSideHybridSearch.cs), carries it.
Only `QdrantVectorStore` implements it; `RetrievalPipeline` prefers it when
`store is IServerSideHybridSearch h && await h.SupportsServerSideHybridAsync(collection)` and **falls
back to hub RRF otherwise** (a dense-only 3.1 collection, or `local`/`postgres`). It is deliberately
**not** a method on `IVectorStore` — the seam already carries three engines and only one can fuse
server-side; the other two must not grow a method they would have to fake. Same "one implementation
behind a seam" shape as `IReranker`. Keyword mode needs no pipeline fork: the store's
`SearchKeywordAsync` is a real sparse-vector search on a hybrid-capable collection and the coarse
scroll on a dense-only one. `RetrievalPipelineTests` pins both the server-side fork and the fallback
with a decorator store, crossing no wire.

**D2 — The sparse vector is hub-computed and IDF is Qdrant's job, so rule 5 held again — zero
dependencies.** [SparseVector](src/InferHub.Coordinator/Vector/Qdrant/SparseVector.cs) turns text
into `{indices, values}` where indices are a stable FNV-1a hash of each token and values are raw term
frequencies. The tokens are **exactly** `InvertedIndex.Tokenize`'s, so "the lexical view of a chunk"
means the same thing under `local` and `qdrant` (`SparseVectorTests` pins that parity). No sparse
model, no corpus statistics threaded through the hub: the collection's sparse vector is declared with
`modifier: idf`, so **Qdrant applies the inverse-document-frequency weighting server-side**. A rare
hash collision merely conflates two terms — acceptable and honest for a lexical branch.

**D3 — Hybrid-capable collections are named-vector collections; 3.1 collections stay dense-only, by
design.** A collection created on 3.2+ has a **named** dense vector (`"dense"`) plus a named sparse
vector (`"sparse"`), so every points/search/retrieve call branches on `CollectionMeta.Hybrid`: a
hybrid collection addresses `dense` by name and writes `{dense, sparse}`, a 3.1 collection uses the
unnamed shape. `QdrantClient.GetCollectionAsync` reads both wire shapes (unnamed `vectors:{size,…}`
vs named `vectors:{dense:{…}}` + `sparse_vectors:{sparse:{…}}`) and reports the flag. A collection
created on 3.1 keeps answering **vector** queries after upgrade and its keyword search stays coarse
until it is re-created or migrated (phase 35) — `QdrantVectorStoreTests` pins the dense-only
compatibility path. The default retrieval mode is still `vector`, so a deployment that sends no
headers and changes no config is byte-identical to 3.1.

**Rule 5 survived again.** Phase 34 added **zero** new dependencies: the sparse vector is hub-computed
over `System.Text.Json`, and the Query API is more hand-rolled REST on the existing `QdrantClient`.

### Phase 40 (the capability seam) — also load-bearing

**D1 — The unit of routing is `(capability, model)`, and a node that declares nothing is read as
today's node.** `NodeCapability(Kind, Models)` rides on `NodeRegistration` and `NodeModels`, both
optional. **Null means "not declared" and resolves to `chat` + `embed` over every reported model —
byte-for-byte the pre-v3.8 semantics.** An *empty* list is a declaration that this node serves
nothing, and the two must never be conflated. The default is materialised in exactly one place,
[NodeCapabilityResolver](src/InferHub.Coordinator/Services/NodeCapabilityResolver.cs), and cached on
the registry entry, which is why **no call site anywhere branches on "is this an old node"**. A
required field with a migration was rejected for phase-33 D3's reason: a fleet is upgraded one box
at a time, and a contract that only works when everything is on the new version has an outage in it.
`CapabilityRoutingTests.ANodeThatDeclaresNothingIsRoutedExactlyAsBefore` is the load-bearing test in
the suite — not the new behaviour, the old one.

**D2 — Capabilities are declared by composition, never guessed, and the only knob is subtractive.**
[BackendCapabilities](src/InferHub.Node/Capabilities/BackendCapabilities.cs) derives them from the
backend's own model list; `Node:Capabilities:Disabled` narrows it. **Nothing infers what a model is
*for*.** Ollama's `/api/tags` does not say, and a name heuristic ("it has 'embed' in it") is
phase-29 D5's capability registry by the back door — built, believed, and wrong for somebody. So an
embedding-only box is expressible in one line of config and is not detected. A hand-maintained
*additive* list was rejected too: it drifts the day someone runs `ollama pull`, and a node that
claims a capability it does not have is worse than one that claims none, because the hub routes to
it and the client gets the error.

**D3 — Work with no Ollama shape gets its own contract; rule 6 is bounded rather than weakened.**
See rule 6 above. `InferenceJob.Kind` with a foreign body was rejected — two lines today, and every
future reader has to discover which jobs the rule is true of. What is *not* duplicated is the
machinery: when phase 41 dispatches a `ToolJob` it goes through the same job registry, the same
stream plumbing and the same failover, and its hub method **must not declare a `CancellationToken`
parameter** (the client-to-server binder trap that hung every stream for several releases). *The
`ToolJob`/`ToolResult` records themselves are deliberately **not** in 3.8 — see the deviation note
below.*

**D4 — A capability nobody provides is a `503` with `Retry-After`; a model nobody holds is still the
`404`.** Phase-25 D4 makes "not allowed" and "does not exist" indistinguishable, and that is an
*authorization* answer. "This model is here but nobody will chat with it" is a *fleet-state* answer,
the same category as saturation — so it gets the saturation shape. Admission runs first, so the 503
can never be used to probe for a model a client's scope hides: it only ever reflects models the
caller already reaches. `Retry-After: 30` is a hint and is documented as one — a node with the
capability may connect at any time, or never.

**D5 — Solo mode enforces the same key at the edge, because one key must not mean two things.**
Phase-37 D9's shape (`Node:MaxConcurrency` is advisory in a mesh and enforced in solo): the meaning
is unchanged and only the enforcer moves, from the router that is not there to the node that is.
Same 503, same `Retry-After`, in both dialects. **Edge only** — solo retrieval still embeds its own
corpus with `embed` disabled, because the node's own corpus is not somebody sending it work.

*Recorded deviations from the phase brief, on purpose:*
- **The `ToolJob` / `ToolChunk` / `ToolResult` / `ToolAttachment` records are deferred to phase 41.**
  The brief listed them here as "contracts only, nothing dispatches them". That is the same category
  the phase's own non-goals refuse for the audio endpoints ("a documented feature that does not
  work"), and a *shipped* wire contract is harder to reshape than one that never shipped — phase 41
  will have a real consumer to shape it. D3's reasoning stands and is recorded above; only the
  records wait.
- **`NodeCapability` carries `Kind` + `Models` and not the brief's `Streaming` / `MaxConcurrency`.**
  Nothing in this phase reads either, and a wire field nobody reads is a field that is wrong by the
  time somebody does.
- **The node declares on the model report, not at registration.** At registration it has not asked
  the backend what it holds, and asking first would mean a node with a dead backend never registers
  at all — the opposite of phase-36 D7, which exists so a broken box is *visible* and unrouted. The
  field is on `NodeRegistration` too, for a node whose capabilities do not follow from a backend.
  A message that carries no declaration never erases one, so the re-register-then-re-report order of
  a reconnect cannot open a window where an embed-only node takes chat.

**Rule 5 survived again.** Phase 40 added **zero** new dependencies and `InferHub.Shared.csproj` is
still empty.

### Phase 46 (text to image — Stable Diffusion on the fleet) — also load-bearing

**The capability seam took a whole new modality with no protocol change**, which is what phase 40 was
built to make possible and is worth saying out loud: `image` is one more `NodeCapability` kind, a
`ToolJob` carries the request, and neither `InferenceJob`, the dispatcher, the router nor the mesh
learned anything.

**D1 — The client surface is OpenAI's Images API, and the extensions are headers.**
`POST /v1/images/generations` on both hosts. `steps`, `guidance` and `seed` travel as
`X-InferHub-Image-*` headers (phase-24 D5's shape, including "an unknown value is a 400, never a
silent fallback") because a body field would collide with whatever OpenAI adds next and would make a
typed SDK's request object wrong. `seed` is *also* a body field, because it is the first thing anyone
reaches for. **`negative_prompt` is a body field and never a header**, and that is rule 7 rather than
taste: it is the caller's own words, and a header is the one part of a request every proxy in the
path writes down by default.

**D2 — `image` is its own capability kind, and the model name is a *recipe id*.** A client sends
`sdxl`, not `stabilityai/stable-diffusion-xl-base-1.0`. A repo id is a location: it contains a slash
every router, path and metrics label has an opinion about, and it changes when a model is re-hosted —
which is not hypothetical, because `runwayml/stable-diffusion-v1-5` was **withdrawn** and one of the
two shipped recipes points at its replacement. Generating is deliberately not editing; phase 50 adds
`image-edit` as a second kind rather than a per-model operation list, because the router filters on
`(kind, model)` and nothing else.

**D3 — A recipe is a model; a manifest is a tool.** `python/recipes/*.json` (repo, **pinned
revision**, pipeline class, aspect buckets, defaults, `cpuViable`) beside
`python/manifests/diffusion.json`. Two files on purpose: the manifest is the operator's ceiling and
is what `Tools:Allowed` names, while recipes are a catalogue the tool reads — collapsing them would
make every new model an entry in `Tools:Allowed`, and a phase-43 profile could then not enable a
model the operator had not pre-named. **A recipe with no `revision` is skipped and logged by name**;
without a pin, "which weights were in 3.14.0" has no answer (phase-39 D9, asked of a Hugging Face
repo instead of a tarball).

**D4 — The response budget is bytes, not a count, and it is clamped by the attachment cap.** `n` is
bounded by `Images:MaxBatch` (4) *and* by an upper-bound estimate — `width × height × 4 × n`,
uncompressed RGBA, which no three-channel PNG can exceed — checked at the edge **before a step
runs**, with the refusal naming the largest `n` that fits. `ImageEdgeOptions.Resolve` then takes the
**smaller** of `Images:MaxResponseBytes` and `Tools:MaxAttachmentBytes`, because the attachment cap
is what sizes `NodeHubLimits.ReceiveSizeFor`, and exceeding a SignalR message cap **tears the
connection down** rather than failing the message. That is the v3.10.0 bug, and an operator raising
one key without the other must not be able to reproduce it. `ImageWireSizeTests` pushes 3 MB across a
real wire and asserts **the node is still registered afterwards** — which is the assertion a green
suite got wrong last time, when phase 41 "proved" attachments with a 16-byte file.

**D5 — There is no URL, and that is rule 4 and rule 7 pointing at the same refusal.**
`response_format=url` is a `400` naming `b64_json`. Serving a URL means the hub keeps the bytes;
keeping the bytes means an image store, a retention window, a deletion endpoint and a question about
whose pictures those are. Considered and rejected: writing them under `/data` and serving them from
the solo API — twenty lines, a nicer demo, and every solo node quietly becomes an image archive on
somebody's laptop (phase-23 D2 refused the same trade for uploaded documents).

**D6 — The hub never decodes a pixel, so there is no image library.** Everything it knows comes from
the worker's result frame. Nothing measures, resizes, re-encodes or validates a raster — which is
what keeps rule 5 intact through a phase that is *about* images. What the edge does validate is the
**request** (the `WIDTHxHEIGHT` grammar, the 64–4096 bounds, the multiple-of-8 rule, the byte
budget), because that is cheap, it is the caller's error, and catching it saves a minute of GPU.

> **Recorded deviation from the phase brief:** the brief had the edge validate a size against the
> recipe's aspect buckets. It cannot — a recipe is a file on the node and the hub has no model
> catalogue until phase 48. The **worker** is the authority, answers `invalid_request` with the
> buckets named, and `ImageRenderer` renders the 400 without reading the message (phase-29 D6). One
> round trip to find out; the alternative is publishing a catalogue over the mesh, which is a phase.

**D7 — CPU is claimed per recipe, from a measurement, never as a checkmark.** `sd15` at 512² is tens
of seconds on a modern core; `sdxl` at 1024² is minutes. So `cpuViable` is a **recipe** field, and on
a CPU-only node the recipes without it are simply **not declared** — the hub never routes to them
(41 D6's withdraw-on-failure, applied before the first failure). `Tools:Image:RequireGpu` defaults
**true** and the worker refuses to start with no CUDA, naming the key to unset; a tool that loads
happily and then serves four-minute requests is a node the fleet keeps routing to, and every caller
pays for the discovery. `Tools:Image:AllowSlowCpu` is the third step for somebody who has read both
numbers.

**D8 — No bundled safety classifier, and if one is ever added it fails the job rather than returning
a black square.** `diffusers`' `StableDiffusionSafetyChecker` returns a **black image** on a
positive, which is disqualifying on its own: the operator gets a bug report rather than a policy
signal, and the failure is indistinguishable from a broken VAE, a bad seed or an OOM. Bundling one
would also mean shipping a model that decides what a self-hosted box may produce — which is precisely
the decision people run a self-hosted box to keep. The docs say so plainly, in the same voice as
41 D7's not-a-sandbox sentence.

**D9 — The fifth image does not stack, and has no Ollama in it.** `inferhub-node:diffusion` is built
from the **plain** node plus PyTorch and diffusers (~9 GB), not from `:tools`. Three reasons, in
order: stacking reaches ~15 GB and every pull pays for it; a card running a diffusion pipeline has no
room for a chat model beside it, so bundling one would ship a combination the docs would have to tell
people not to use; and **the mesh is the composition mechanism** — run `:diffusion` on the card and
`:ollama` next to it, and phase-40 routing sends `image` to one and `chat` to the other. A combined
`:all` image is deferred and named. `BundledNodeTests` asserts the four older images never learned
about torch, and that this one's venv **imports at build time** — the v3.10.0 failure, turned into a
`docker build` step rather than a thing to remember.

**Metering: `megapixel_steps`, and deliberately not "images".** `width × height × steps / 1e6`,
summed over what the worker actually produced (a recipe may clamp either, and metering the *asked*
figures would bill for work not done). A 512² image at 4 steps and a 2048×1024 one at 30 steps are
both "one image" and the second is **47×** the work; a counter that bills them the same is wrong in a
way that scales with how much somebody uses the expensive path. `ClientLimits.MegapixelStepsPerDay`
rejects with phase-25 D4's shapes. **Phase 46 needed no Postgres migration** — phase 42's generic
`units` + `unit_kind` pair took a fourth unit, which is the payoff of not having added a column per
unit.

**Rule 7, in a form it had not met: a prompt is content.** A transcript is content because it is what
somebody said; a prompt is content because it is what somebody *wanted*, and the picture is the
answer. Nothing logs a prompt at any level, on either host; the log line carries the model, the image
count, the megapixel-steps and the outcome. `ImagePrivacyTests` asserts it against a capturing logger
at `Trace` and checks the scratch directory is empty after success **and** after failure.

**Rule 5 survived again.** **Zero** new `PackageReference`, no image library anywhere in C#, and
`InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`. PyTorch is a
subprocess, exactly as phase-41 D1 requires.

> ### v3.14.1 — what v3.14.0 got wrong, and the two mechanisms it needed
>
> **v3.14.0 was dead on arrival for `sdxl` on a fresh volume.** Found by pulling the published image
> and running it, which is the sixth time (v2.5.1, v3.0.1, v3.5.1, v3.10.0, the phase-32 D7 note).
>
> **Bug 1 — `variant` is not `dtype`, and conflating them doubled every download.** `dtype` is what
> the weights are cast to in memory; `variant` is which **files** are fetched. These repos carry
> both `unet/diffusion_pytorch_model.safetensors` (fp32, 10.3 GB for SDXL) and
> `…fp16.safetensors` (5.1 GB), and `torch_dtype=float16` alone takes the fp32 one and casts it
> down. The recipe said `"dtype": "float16"`, the docs said "~7 GB fp16", and 13 GB landed in the
> cache. **Both recipes now carry `"variant": "fp16"`**, `_from_pretrained` is the one place a
> pipeline is constructed so serving and prefetching cannot drift, and a repo without the variant
> falls back **loudly**.
>
> **Bug 2 — weights were fetched inside the request that first named the model.** The manifest
> allows 900 s and that budget included the download: two consecutive `sdxl` calls each returned
> **502 after 899.99 s**. Phase 48 D4 in the roadmap says *"weights are pulled by an explicit
> command, never lazily inside a request"* — written for FLUX at 24 GB, and phase 46 shipped the
> lazy path anyway on a model big enough to hit it.
>
> The fix is that **a recipe is declared only once its weights are proven loadable**, with a
> background thread fetching and the worker re-declaring as each lands. Readiness is a marker file,
> because the obvious checks lie: `snapshot_download(local_files_only=True)` and
> `DiffusionPipeline.download(local_files_only=True)` both return happily with the UNet entirely
> absent (verified against a half-downloaded cache). Only `from_pretrained(local_files_only=True)`
> asks the question the next request asks — and it *is* the load, so the prefetch does it once and
> records the answer.
>
> **Two node-side mechanisms, and one of them had been dead code since v3.9.0.**
>
> 1. **A late `ready` re-declares** (`ToolWorkerProcess.CapabilitiesRedeclared` →
>    `ToolWorkerPool.RefreshCapabilities`). `ExecuteAsync`'s default branch used to discard it with
>    a comment naming it. The narrowing clamp is unchanged and still applied on the node: a worker
>    cannot widen its own grant by re-declaring any more than it could at handshake.
> 2. **The maintenance loop now pings idle workers.** Phase-41 D6 specified a ping/pong liveness
>    probe, `PingAsync` was written for it, and **nothing ever called it**. It matters here because
>    an idle worker has nobody reading its stdout, so a late `ready` sits in the pipe until
>    something drains it. **The probe takes the concurrency slot** — without that, maintenance
>    holding the only worker out of the idle stack would let a concurrent request start a second
>    process, which is two copies of a multi-gigabyte model on one card.
>
> `ToolRedeclarationTests` drives all of it through a real child process, including the guard on the
> guard (a worker that never re-declares must not make the node re-report twice a minute forever).

### Phase 47 (jobs, progress, cancel) — also load-bearing

**D1 — The async surface is InferHub's own, under `/api`, and that is not a second dialect.**
OpenAI has no asynchronous Images API to adopt. Phase-21's rule is "adopt the dialect clients already
speak", and where there is none the rule does not say "invent an OpenAI-shaped one" — it says what
phase-40 D3 said about `ToolJob`: **work with no existing shape travels as its own honest contract.**
So `/api/images/jobs` and its four companions, in the house style of
`/api/admin/models/{model}/ensure`, under the `/api` prefix `BearerApiKeyMiddleware` already guards.
**Considered and rejected: a `background: true` field on `/v1/images/generations`** returning a
non-OpenAI body — one route answering two incompatible shapes depending on a flag, which every typed
SDK gets wrong. The repo has refused this trade five times (21 D3, 22 D1, 34 D1, 38 D2, 40 D3).

**`/v1/images/generations` became "submit a job and wait for it", and nothing about it changed for a
caller.** Both surfaces queue in one line and are metered by one code path; two paths to a GPU with
two ideas of fairness is how a fleet grows a fast lane nobody documented. `ImageSyncCompatTests` pins
the envelope field-for-field. What is new is a bound: past `Images:SyncMaxWaitSeconds` (120) it is a
`503` naming the job id and the async route, and **the job keeps running** — cancelling it because an
HTTP client got bored is the caller's decision.

> **The refactor lost a status once, and the suite caught it.** Flattening a failed job to a `502`
> at the sync edge dropped the `400` a worker's `invalid_request` earns and the `503` a busy tool
> earns — phase-29 D6's inference by the back door, reached by *discarding* information rather than
> by guessing. `ImageJobFailure` now rides on the record: the node states the kind, `ImageRenderer`
> decides the status, and the job carries that decision to whichever surface asks.

**D2 — Progress travels on the existing streams; there is no new transport.** Worker → node:
`progress` frames (`{step, totalSteps}`), a new frame type and **additive** — a worker written
against 3.14 never sends one and behaves exactly as it did. Node → hub: the same `ToolChunk` contract
phase 41 built, on an ordinary `NodeHub.ToolJobProgress` invocation. Hub → client: SSE framed by
hand, exactly as phase-21's SSE and phase-28's exposition format are, because a stream of six-field
objects does not justify a dependency. Phase-26 D2's precedent is exact (a model pull relays progress
on an existing SSE channel) with one deliberate difference: image progress is **client-facing**, so
it does not go on `/api/admin/stream` — an admin channel carrying tenants' job ids is an
authorization mistake waiting for somebody to notice.

*`progress` is a separate frame type from `chunk`, and the distinction is load-bearing:* a chunk is a
partial **answer**, and putting "7 of 28" into the body of a streaming tool response would hand a
client that expects content something that is not. `ToolExecutor.StreamAsync` names it explicitly for
a sharper reason — the default branch there is the *terminal* one, so a progress frame falling
through would end the stream at step one.

**Image jobs dispatch *blocking*, not streaming, and that is not a contradiction.** A streaming tool
response deliberately carries no attachments (phase-41's `StreamAsync` refuses them), and an image
*is* an attachment. So the answer is one `ToolResult` on the result path and the progress arrives out
of band — `Dispatcher.toolProgress` is checked only after `pendingToolStreams` misses, so every job
with no sink registered takes the 3.14 path byte for byte.

**D3 — Cancel is cooperative first and a kill only as a last resort, because the weights cost a
minute.** A `cancel` frame (node → worker) carrying the request id; the worker honours it from its
per-step callback and answers `error` coded `cancelled`, and **is then still alive and still holding
its weights**. Past `Tools:CancelGraceSeconds` (20) it is terminated and restarted — the existing
path. **Killing first would be simpler and is wrong**: it punishes the *next* caller for the first
one's change of mind, and the punishment grows with every model the catalogue gains. Phase-41
deviation 6 established the opposite instinct for a worker that blew its deadline — that one is by
definition not cooperative — and this is the complementary case, stated so the two do not read as a
contradiction later.

**Cancellation is best-effort and the API says so.** `running → cancelling → cancelled | succeeded`
is legal, and `ImageJobTests` asserts the `succeeded` outcome as **legal, not flaky**. Discarding a
finished image to honour a state name would be worse than reporting what happened.

*The node's cancel plumbing is the part that is easy to get subtly wrong:* `ToolExecutor.RunAsync`
drives the read loop on a token that is **not** the caller's. The caller's token sends the frame and
arms the grace clock; only when that expires does the hard token fire and take the worker down.
Wiring the caller's token straight through would make every cancel a kill with extra steps.

**D4 — Per-step progress is free; previews are not, and are off.** `{step, totalSteps}` costs a
callback. A preview costs a VAE decode per step (~10–15% of the run) and produces intermediate
*content*, which is rule 7's business. *Recorded deviation: the preview opt-in is **not** in 3.15.*
The brief specified `X-InferHub-Image-Preview: every-n` and `Images:AllowPreview`; shipping a header
whose only effect is more of what a caller already gets, on a fixture with no VAE to decode, would be
a documented feature nobody could verify — the category phase 40's non-goals refuse by name. The
refusal is what survives here: previews are content, they are off, and a phase that adds them adds
them with a worker that actually produces one.

**D5 — The queue is per capability and it is FIFO, deliberately.** A queued image job is a **202 with
a queue position**, not a wait-then-503: the client already accepted asynchrony, so making it retry
would be strictly worse than telling it where it is in line. FIFO, not shortest-job-first (a stream
of 4-step requests would starve a 50-step one invisibly) and not fair-share-by-client (needs a tenant
weight phase 25's client model does not have). `Images:Jobs:MaxQueueDepth` (32) bounds it and a full
queue is `503` + `Retry-After`, the same status and header as every other limit here.

*Recorded deviation: the queue is `ImageJobRegistry`'s own, not a path added to `RequestQueue`.*
`RequestQueue` answers "wait for a model's fleet to free a declared concurrency slot, then 503",
which is a different question with a different answer shape; the fairness this phase needs is "one
image job per capable node, head-of-line". Grafting a 202 onto a class whose whole contract is a
`QueueOutcome` enum would have made both harder to read. The pump is event-driven — it runs on every
submission, completion, cancel and loss rather than on a timer — so a freed GPU is used in
milliseconds instead of at the next tick.

**D6 — The job store is in memory, bounded, expiring and read-once. This is what keeps rule 4
whole.** See the note at rule 4 above for why it is not a fourth exception. The byte budget is
enforced **on insert**, not on a timer: a timer means the ceiling is a suggestion for one sweep
interval, and one sweep interval of 4096² batches is how a hub gets OOM-killed. Eviction sets the job
to `expired` with a reason so a late arrival is a `410` that says what happened rather than a `404`
that reads like a bug.

*Recorded deviation: `ImageJobStore` lives in `InferHub.Shared`, not in `InferHub.Coordinator` where
the brief put it.* Phase-38 D2's reasoning: solo mode serves the same five routes, and two
implementations of "when do the bytes go away" is one answer too many. It is a plain class with no
ASP.NET and no logging package, so rule 2 holds and **`InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`**. What stayed per host is everything that needs a fleet —
routing, dispatch, metering, the ledger — which is exactly the line phase 38 drew.

**D7 — A node failure mid-job is a failed job with a reason, and never a silent retry.** Phase-21
D3's pre-stream failover retries a request that has not produced output; an image job that died at
step 22 has produced none and is *technically* retryable — and retrying it would silently double the
GPU-minutes and the ledger units for one request. So **no automatic retry once a job is `running`**:
it fails `node_lost` and the client decides. A job still `queued` is re-routed, because nothing has
been spent. There is deliberately **no hook for this**: `NodeHub.OnDisconnectedAsync` already faults
the pending dispatch through `Dispatcher.FailForConnection`, and that lands in the one `catch` that
owns the decision. `ImageJobTests` takes a real node away mid-job and asserts both the reason **and
an empty ledger** — a silent retry would have doubled those numbers rather than left them empty.

**The echo worker grew a slow, cancellable, progress-emitting image mode** (`--image-step-ms`,
`--ignore-cancel`), and that is what makes the whole job model testable with no GPU and no weights —
phase-41's echo-worker discipline, and the reason this phase's suite runs in CI. Two things only a
**real child process** can produce are exactly what this phase is about: a frame arriving *while* a
request is in flight, and a worker that is still warm afterwards. The Python reference library gained
the same (`request.progress`, `request.raise_if_cancelled`, `Cancelled`), and its loop now reads with
`readline()` rather than `for line in stdin` — **the iterator protocol keeps a read-ahead buffer, and
the frame two readers would lose between them is the cancel.**


### Phase 53 (the streamed-upload contract) — the pointer, and one correction

The contracts live here; both decisions live elsewhere. **The hub's half** — the pull stream, the two
paths, the ordering rule, the mixed-fleet declaration, the derived ceilings and why the image routes
are excluded — is in `src/InferHub.Coordinator/CLAUDE.md` (phase 53, D1–D6, D9). **The node's half**
— writing the scratch file, its own ceiling, and solo — is in `src/InferHub.Node/CLAUDE.md`.

What is *here* is [AttachmentChunk](src/InferHub.Shared/Contracts/ToolAttachment.cs) — `start` /
`data` / `end` frames — and `ToolJob.HasStreamedAttachments`. **The frames describe the body in the
order it arrives rather than the job carrying a list of attachment references**, and that is a
recorded deviation from the brief with a reason: how many parts a multipart body has, and what they
are called, is not knowable until it has been read, and reading it to find out is the buffering the
phase removes.

### Phase 55 (seam repair) — the pointer, and what is decided here

The decisions are in `python/CLAUDE.md` (phase 55, D1–D6), because the mechanisms are the worker's
and nothing here has ever decoded a pixel (46 D6). What lives *here* is the vocabulary and the wire:
[SeamRepairModes](src/InferHub.Shared/Images/SeamRepair.cs) — the four words, which of them a
**caller** may say versus which an **operator** may set, `Permits` (exact match, plus `any`), the
refusal sentence, and the content route's three headers written in one place so the hub and a solo
node cannot disagree about what a fetched panorama says about itself.
`ImageExtensions.SeamRepair` is parsed beside the other four extension headers, which is why solo
mode got it the same day (41 D8's pattern), and `ImageJobImage` / `GeneratedImage` carry
`SeamDeltaBefore` + `SeamRepair` **only when a repair ran** — absence stays absence (28 D5), and
`seam_delta` stays the *current* image's so a client that never heard of phase 55 reads the field it
already knew and gets a true answer.

> **One correction to phase-40 D4, measured on .NET 10 rather than reasoned about.** That decision's
> sentence was *"no temp file, no cache"*, and it was true of the code written here and false
> underneath it: `HttpRequest.ReadFormAsync` buffers a file section over
> `FormOptions.MemoryBufferThreshold` (**64 KB**) into an `ASPNETCORE_*.tmp` file in the process's
> temp directory. A 3 MB upload produced one of 3 145 570 bytes; a 32 KB upload produced none. So
> from v3.9 to v3.20 **every real audio and image upload did touch the hub's disk**, briefly, through
> the framework. The streamed path does not, and `ToolUploadTests` asserts no temp file the size of
> the upload appears. The buffered path still behaves this way — it is ASP.NET's, not ours — and the
> honest statement of rule 7 for it is "held for the dispatch and dropped", without the temp-file
> clause.

### Phase 56 (durable image jobs) — also load-bearing

**D1 — `Images:Jobs:Persistence=file` is rule 4's fourth recorded exception, and the argument lives
in the rule.** See the root `CLAUDE.md`. What is here is the mechanism:
[IImageJobArchive](src/InferHub.Shared/Images/ImageJobArchive.cs) with `NoImageJobArchive` as the
default and [FileImageJobArchive](src/InferHub.Shared/Images/FileImageJobArchive.cs) behind it —
`IProfileStore`'s and `IAffinityStore`'s shape, third use. `ImageJobStore` takes the archive and is
otherwise the phase-47 class; the guard is on `IImageJobArchive.Enabled` *before* a record is built,
so the default path allocates nothing. **Considered and rejected: on by default with a short
window** — every other exception defaults to `none` because an operator who has not chosen to store
user content has not consented to storing it.

**D2 — Durability does not extend retention, and the window is applied on load.** A hub down for an
hour comes back and expires everything past `RetentionSeconds` *before it serves the first request*,
deleting the files as it goes. **Considered and rejected: letting the five-second sweeper catch up**
— that is a window in which a week-old picture is fetchable, and it puts the only enforcement of the
phase's central promise on a timer that starts after the endpoints do. Resurrection is also the bug
nobody would find: it happens in the crash-recovery path, on a box nobody is watching, and it looks
like the feature working. The **byte ceiling** is re-applied on load for the same reason — an
operator who lowered it while the hub was down gets the lower one honoured on the first boot rather
than on the first render.

**D3 — What is persisted is the record and the bytes, *never* the request — so nothing in flight can
resume.** `ArchivedImageJob` has no field that could hold a prompt, a negative prompt, an uploaded
picture or a mask, and there is deliberately no flag to add one: **a field is an invitation** (25 D3,
and rule 7's first meeting with a disk). The direct consequence is the one a caller sees: a job that
was `queued`, `running` or `cancelling` comes back **`failed` with `hub_restarted`**, carrying a
sentence that says it was not resumed and why. That is 47 D7 one level up — a silent re-dispatch
would spend the GPU minutes and the ledger units twice for one request — and here it would
additionally require writing down the thing rule 7 forbids. The worker's **error** is kept, and is
not a hole in that: it is the model's sentence about a size, a licence or a busy card, never the
caller's words (`ImageRenderer` leaves `revised_prompt` null by policy) — 49 D2's line, that a
constant of the model may be recorded where the prompt may not.

**D4 — Read-once means read-once from the disk too.** Delivery, LRU eviction, the retention sweep
and a failure that clears images each unlink the file in the same operation that drops the bytes,
under the store's own lock. **This is why the format is a file per job rather than
`FileProfileStore`'s append-log-and-snapshot**: an ops log would have to be compacted to reclaim a
delivered picture, so "read once" would leave the pixels on disk until a compaction ran.

**D5 — `file` only; the HA consequence is stated rather than mitigated.** No `postgres`: half a
gigabyte of PNGs in a `bytea` column is WAL amplification per render and a `pg_dump` of the usage
ledger's database that now contains pictures. Under `Cluster:Enabled` image jobs are **per
instance** — 32 D1's "the same Postgres, so no new source of truth" does not stretch to a local
directory, and a promoted standby will answer 404 for the old primary's jobs. A shared filesystem is
a deployment choice this project neither requires nor tests.

**Three sentences that had become false were changed rather than left standing**, which is the
house rule about caveats: the 410 body's "nothing was written to disk" is now conditional on the key
(and is still the whole truth under the default), the console's memory-only line is chosen from the
`persistence` field the listing now carries, and `Images:Jobs` in both `appsettings.json` says what
`file` costs.

**Rule 5 survived again.** **Zero** new `PackageReference` — `System.Text.Json` and `System.IO` ship
in the shared framework — and `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`. Rule 2 holds too: the archive reports its failures through an
`Action<string, Exception>` the host passes, so nothing here needs `ILogger`, and a full disk costs
the archive rather than the render.

### Phase 57 (the video seam) — also load-bearing

**D1 — The client surface is OpenAI's Videos API, because unlike its Images API it already has an
asynchronous one. This is the decision the phase turns on, and it is a *reversal of nothing*.**
Phase 47 D1 built `/api/images/jobs` on an explicit premise — *"OpenAI has no asynchronous Images
API to adopt"* — and phase-21 D3's rule is to adopt the dialect clients already speak and to invent
only where there is none. For video there is one, async by construction: `POST /v1/videos` answers
with a video object carrying `status` and `progress`, `GET /v1/videos/{id}` polls it,
`GET /v1/videos/{id}/content` fetches the bytes, `DELETE` drops it. So nothing is invented here.
**Considered and rejected: `/api/videos/jobs` mirroring the image five** — the smaller diff, and it
makes `client.videos.create(...)` in every OpenAI SDK useless against a hub that can serve it.

[VideoRenderer](src/InferHub.Shared/Images/VideoRenderer.cs) writes the object in **one** method
building **dictionaries**, which is 49's `ImageRenderer.Envelope` lesson applied *before* the bug
rather than after it: which keys are present is part of the contract, and the last time it was
inherited from a serializer policy the hub emitted `revised_prompt: null` while a solo node omitted
it, for three releases, under a parity suite.

**Two of the dialect's routes are `501`s that name the reason.** `GET /v1/videos` enumerates a
client's jobs and this project holds no such index (phase 51's images listing is a console-scoped
exception no SDK calls); `POST /v1/videos/{id}/remix` needs the request kept after the job ends,
which 56 D3 forbids in the sentence it is built on. A `404` would read as "an old hub".

**The mapping loses exactly one distinction, on purpose.** `cancelling` renders as `in_progress`,
because a job asked to stop is still running and may finish (47 D3), and inventing a status word the
dialect's typed enums do not have would break the reason for adopting it. `expired` renders as
`completed` with nothing to fetch: the render *happened*, and calling it `failed` would say it did
not. `progress` is capped at **99** until a terminal state — a client that sees 100 and stops polling
has stopped one round trip before the bytes exist.

**D2 — A video is an `IImageRequest`, and the rename is refused here in writing.**
`VideoGenerationRequest` implements phase 50's interface, so the queue, the per-step progress, the
cooperative cancel, the read-once retention and phase 56's archive are the *same code*.
**Considered and rejected: a parallel `VideoJobRegistry`** — two queues on one card is two ideas of
fairness, which is what 47 D1 built a single path to prevent, and 56's archive would grow a second
answer to "when do the bytes go away". **Considered and rejected: renaming the eleven `ImageJob*`
types to `MediaJob*`** — 52 D4's rule, that a phase adding a modality is perfect cover for "while I
was in there", and a bisect holding a new pipeline *and* four hundred renamed references is a bad
afternoon. The names are wrong by one word; the wrongness is recorded rather than papered over.

**A job is scoped to the surface that submitted it as well as to its client.** `ImageJobStore.Find`
and `ForClient` take a capability predicate, so a video id on `/api/images/jobs/{id}` is the *same*
404 an unknown id earns — phase-25 D4's reasoning about tenancy, applied to surfaces: "that id is
real but it is a picture" tells a caller something about an id they were never meant to reason about.
Without it the phase-51 Images panel would list clips it renders as broken pictures.

**D3 — The grids are not the image grids, and `VideoSizes` exists because of it.**
`WanPipeline.check_inputs` in the pinned `diffusers==0.36.0` requires both sides divisible by **16**
where every image pipeline here downsamples by 8, so `840x480` is a perfectly good `ImageSize` and an
invalid video size. Parsing a video size through `ImageSize.TryParse` would move that refusal to a
`ValueError` four minutes into a job. **The duration is the worker's**: `durations` maps whole
seconds to frame counts because a latent video pipeline puts frames on a **4k+1** grid, so 81 frames
at 16 fps is 5.0625 s while OpenAI's `seconds` is a small integer — the label names an offer and the
**response reports the measurement**. An unoffered value is refused naming the list, never rounded
(49 D3).

**D4 — Two units on one job, and the quota is the one the card actually spends.**
`UsageUnitKinds.VideoSeconds` joins `MegapixelSteps` rather than replacing it — phase 42's audio
precedent exactly. A video transformer denoises the **whole latent stack** every step, so
`width × height × frames × steps / 1e6` is literally what the GPU did, and a 5-second 832×480 clip at
30 steps is ~970 megapixel-steps against an SDXL image's 31. **Considered and rejected: a unit of its
own for the quota** (the track index guessed `frame_seconds`) — a second answer to a question already
answered, and it would let a client with an exhausted image budget spend the same card under a
counter nobody had set. `video_seconds` is what a human asks about and cannot be derived from the
other. `AdmissionControl` still had to learn the kind, because its `default` branch counts whatever
reaches it as **tokens**. **`VideoSecondsPerDay` was deferred until a catalogue existed and landed in
v3.27** — a quota knob for a one-model catalogue would have been a key that was wrong by the time 58
shipped. Both units are gates now; see 59 D3 in `src/InferHub.Coordinator/CLAUDE.md`.

**Recorded deviation: there is no `Videos:` config section.** The brief's task list named
`Videos:MaxResponseBytes`; a video is one attachment over the wire an image already uses, so it is
bounded by `Images:MaxResponseBytes` clamped by `Tools:MaxAttachmentBytes` (46 D4). Two keys for one
ceiling are two numbers an operator can raise independently — the v3.10.0 connection-tearing bug,
rebuilt.

**Rule 5 survived again.** **Zero** new `PackageReference`, no codec anywhere in C#, nothing that
decodes a frame, and `InferHub.Shared.csproj` is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.

### Phase 59 (the job view) — the one decision that is this library's

**D6 — A job's row names the route *its own capability* is fetched from.** `ImageJobView.Describe`
hard-coded `/api/images/jobs/{id}/content/{index}`, which for a video job is a 404 with a plausible
shape — one job model (57 D10) means two fetch surfaces, and the view is where that arrives. It now
emits `/v1/videos/{id}/content` for a video job, plus `capability` on the row and `seconds` on the
output where the worker measured one (absent on a picture: a zero would be a measurement nobody
made). The rest of phase 59 is the coordinator's and is recorded there.

### Phase 61 (the upstream dialect seam) — the one decision that is this library's

**D3 — A cloud provider is an [`IUpstreamDialect`](src/InferHub.Shared/Upstream/IUpstreamDialect.cs):
Ollama JSON in, Ollama JSON out, five members, no ASP.NET.** The shape is not invented — it is what
`OpenAiUpstreamClient` already had, because the node's `OpenAiBackend` and the hub's provider
dispatcher have both driven it since 22 D1. Naming it is what lets phase 63 add Anthropic's
`/v1/messages` and 64 add Gemini's `:generateContent` without either touching a dispatcher, a router
or an endpoint — and what lets phase 67 give the node the same four providers by *composing* the
same clients rather than writing a second set.

Ollama JSON on **both** sides is the point, and it is rule 6 arriving here rather than being
weakened: a provider is an *upstream-facing* dialect translated at the boundary, so the mesh's
internals never learn a second wire format exists. An implementation that handed back its own
envelope would be asking for the polymorphic job payload 22 D3 costed out and refused.

**Considered and rejected: waiting for 63 to extract the interface**, on the grounds that one with a
single implementation is a guess. It is not a guess when the next two phases are named and written
down; the alternative is that 63 rewrites the dispatcher it was meant to plug into. **Also rejected:
putting it in the coordinator** — the node needs the same seam in 67, and moving it later means
moving it across a project boundary while two implementations exist. Rule 2 and rule 5 both hold: a
plain interface over `HttpClient`, and `InferHub.Shared.csproj` is still an empty
`<Project Sdk="Microsoft.NET.Sdk">`.

### Phase 62 (two ways an OpenAI-compatible upstream is not OpenAI) — found by reading OpenRouter's docs

Both are `OpenAiUpstreamClient`'s and both were live for every `openai-compatible` upstream, not
only the new one. The provider *configuration* half of the phase is the coordinator's (62 D1/D2/D5).

**D3 — `error.code` is read as either a number or a string, in the one envelope both vendors use.**
OpenAI writes `"rate_limit_exceeded"`; OpenRouter writes `429`. `OpenAiErrorBody.Code` is
`string?`, so the whole envelope threw, `Describe` caught its own `JsonException` and fell back to
the **raw body** — the one sentence saying what to fix, delivered buried in the JSON it arrived in,
which is 29 D6's wall of backslashes reached by another route. `LenientErrorCodeConverter` reads
both; writing is unchanged, because a code this project produces is a string.
**Considered and rejected: a second error envelope** — the field is spelled `code` in both and means
the same thing, and what differs is a JSON scalar type, which is not a schema. **Also rejected: try
one shape then the other** — two deserializations and a silent preference between them, to avoid
writing down that servers disagree about this.

**D4 — A failure that arrives *after* the response headers throws, rather than ending the stream.**
OpenRouter reports one as an SSE frame carrying a top-level `error` and `finish_reason: "error"`;
`ReadFramesAsync` read that as a terminal delta, so a request that died at token 40 returned **200
and looked finished**. `ThrowIfErrorFrame` raises `OpenAiUpstreamException` mid-iteration, and both
callers already know what to do with that — the hub's `ProviderDispatcher` writes a terminal error
chunk ("a terminal error chunk beats a hung stream") and the node's `OpenAiBackend` carries it back
as a failed job. **Considered and rejected: yielding an Ollama error chunk from here** — it reaches
the same place, and it makes this interface's contract "an error is a value here and an exception
there", which the next implementer has to discover.

*The status is the upstream's own when it named a plausible HTTP one and 502 otherwise; nothing is
inferred from the error **text**, which is 29 D6 unmoved.* The check is behind a substring test for
`"error"` so it stays off the per-token path, and `AnOrdinaryStreamStillEndsWithADoneChunkAndNothingElse`
is the guard on the guard — a delta whose *content* mentions the word must not become a 502.

**Rule 5 survived again.** **Zero** new `PackageReference`; the converter is `System.Text.Json`.

### Phase 63 (Anthropic's own dialect) — the second `IUpstreamDialect`, and what reading the docs changed

`src/InferHub.Shared/Anthropic/` — the DTOs, the translator and `AnthropicUpstreamClient`. The
provider *configuration* half (the type, `MaxTokens`, the absent id-shape check) is the
coordinator's (63 D2/D8). **Zero new `PackageReference`**: `HttpClient`, `System.Text.Json`, and an
SSE parser written by hand, exactly as 22's was and 33 D2's connector before it.

**D1 — `/v1/messages` is a second dialect, not a second client, and nothing outside this folder
learns Anthropic exists.** `ProviderDispatcher.Dialect` gained one arm; the router, the endpoints,
the affinity keys and the retrieval pipeline gained nothing, which is 61 D3 paying for itself the
first time. The one thing the seam lacked was a way to catch a dialect failure without naming
OpenAI, so `UpstreamDialectException` is now the base of both — **a base rather than a rename**,
because 57 D10 refused exactly that rename and the two suites asserting `OpenAiUpstreamException`
keep passing untouched. **Considered and rejected: pointing `OpenAiUpstreamClient` at Anthropic's
compatibility endpoint**, which is what an operator can already do and is the thing the track exists
to improve on: that layer flattens four token counts into two, so `/api/admin/usage` disagrees with
the invoice and the first question a user asks is why.

**D3 — Every `system` message is lifted to the top-level `system` field, in order.** Anthropic's
`messages` has exactly two roles. **Considered and rejected: the mid-conversation
`{"role":"system"}` form** — it is real, and it is a **400 on Sonnet 5** while working on Opus 5 and
Opus 4.8, so adopting it would make the translation depend on which model the operator happened to
map. A hub that guesses right for two of the mapped models is worse than one that treats all of them
alike.

**D4 — The options Anthropic does not have are dropped, not approximated.** `temperature`, `top_p`,
`top_k`, `stop` → `stop_sequences` and `num_predict` → `max_tokens` translate; **`seed`,
`presence_penalty` and `frequency_penalty` are dropped**, because Anthropic 400s an unknown
top-level parameter. **Considered and rejected: refusing a request that carries one** — it breaks a
working client over a knob the vendor never had. **Also rejected: approximating `seed`**, which
offers a reproducibility promise the upstream does not make, in the field a caller reads as though
it did. The drop is documented in the README and on the site, which is the only channel there is.

**D5 — Token counts are *taken* from the last `message_delta`, never summed, and the cache pair is
not folded in. Read on the day, and it contradicts the OpenAI path's instinct.** OpenAI sends one
usage-only chunk at the end. Anthropic sends `input_tokens` in `message_start` — **with
`output_tokens: 1` already on it** — and then a **cumulative** `output_tokens` in every
`message_delta`, which the vendor's docs say in a warning box. Summing over-reports every stream,
and an invented number sitting in the column beside measured ones is how a usage report stops being
evidence (25 D3). `cache_creation_input_tokens` / `cache_read_input_tokens` are reported and priced
separately and are **not** added into `prompt_eval_count` — a total this hub composed would match no
line on the invoice. A stream with no usage at all yields no counts, not zeros (v3.13.1).

**D6 — There is no `[DONE]`; `message_stop` ends the stream, an unknown event is skipped, and an
`error` event throws.** Three ways Anthropic's SSE is not OpenAI's, which is why `ReadFramesAsync`
is **not** reused: its sentinel never arrives and its error envelope is the wrong one. What *is*
reused is 62 D4's contract — the hub writes a terminal error chunk, the node carries a failed job —
and `overloaded_error` is the frame that would otherwise arrive as a 200 that looks finished.
Skipping an unknown event is the vendor's own versioning policy honoured rather than discovered.
**Considered and rejected: one frame reader with a mode flag** — two frame contracts wearing one
function, with the flag read on the per-token path.

**D7 — There are no embeddings, and the refusal names the reason once.** `EmbedAsync` throws a 501.
Nothing on the hub reaches it (an embedding request goes to `EmbeddingDispatcher` and the fleet), so
the sentence is written for **67**, where a chat-only provider becomes a declared capability and 40
D1's router answers with a 503 that names it. **Considered and rejected: a 503 with `Retry-After`**
— "try later" for an endpoint that will never exist. **Also rejected: an empty vector**, a wrong
answer shaped like a right one, in the one place nobody notices for weeks.

*Phase 29's base64 magic-byte sniff moved to `Upstream/Base64MediaType.cs`* — Anthropic's
`{"type":"image","source":{"media_type":…}}` wants the same answer a data URL did, and the
alternative was four signatures in two dialects, diverging the first time a fifth format arrives.
**Not translated, deliberately:** tool calls, thinking blocks, `cache_control`, PDFs and citations
(track-level deferrals). **No live provider was called by any test** — every payload in
`AnthropicDialectTests` is a recorded one (track D6); the first real key is phase 68's.

### Phase 64 (Gemini's own dialect) — the third `IUpstreamDialect`, and the day the native surface turned out to be the legacy one

`src/InferHub.Shared/Gemini/` — the DTOs, the translator and `GeminiUpstreamClient`. The provider
*configuration* half (the type, `ThinkingBudget`) is the coordinator's (64 D6). **Zero new
`PackageReference`**, for the third dialect running.

**D1 — `:generateContent` is the dialect, and it is now the vendor's *legacy* surface. Reading the
docs on the day found that, and it is written down rather than quietly worked around.** Google's
current documentation calls the **Interactions API** generally available and recommended for new
projects, says all new models and agentic features launch there, and says of the older surface:
*"While it is now considered legacy, the original `generateContent` API remains fully supported"* —
**no removal date published**. The track was written believing `:generateContent` *was* the native
API; it is the previous one, and the phase went ahead with it on three grounds. **(1)** It is the
surface more than one platform shares — the same verbs are Vertex AI's, and a `BaseUrl` override
reaching those is the configuration 63 D8 refused to break; Interactions is first-party-only today.
**(2)** What Interactions adds is exactly what this track defers: `steps[]` of thoughts, function
calls and tool results collapses to one text output for an Ollama-shaped mesh (rule 6), and
server-side `previous_interaction_id` is conversation state a hub that retains nothing (rule 7) must
not own — we would pay for the richer surface and discard what makes it rich. **(3)** It changed its
own schema three months ago (`outputs[]` → `steps[]` under an `Api-Revision` date header, old schema
removed 2026-06-08); a dialect whose stability depends on pinning a date, tested only against
recorded payloads (track D6), goes stale in silence. **Said out loud:** the day this hub wants
Gemini's agentic surface, *this* client is not the one that gets extended — that is a
`Type: "gemini-interactions"` and a phase of its own.

**D2 — The model is a URL path segment, so it is normalized rather than checked, and that completes
a three-way contrast.** `GeminiTranslator.ToModelPath`: **an id containing a `/` is a path and is
used as written; a bare id gets the `models/` prefix.** One rule accepts `gemini-3-pro`, the
`models/gemini-3-pro` the listing returns and a Vertex `publishers/google/models/…`, and
`ListModelIdsAsync` hands back the vendor's own `name` so a console row is pasteable into a
`ModelMap`. The contrast worth keeping: **62 D5 checks a shape** (419-of-419 evidence for one
namespace), **63 D8 checks nothing** (one model, three legitimate spellings), **64 normalizes** (the
id is structural, and getting it wrong is a 404 naming `models/models/gemini-3-pro`, which nobody
typed). **Considered and rejected: a `gemini-` prefix check** — 48 D1, as 63 D8 argued it.

**D3 — `?alt=sse` is part of the streaming path, because without it the endpoint is not SSE.**
`:streamGenerateContent` with no `alt=sse` answers with a **JSON array** and never emits a `data:`
line. The query is a constant beside the verb. There is no `[DONE]` either: the stream ends when the
body does.

> **v3.32.1 corrects this decision's own sentence.** D3 as shipped said an SSE reader "does not
> fail — it *waits*, until the timeout". **It did not wait.** Every line was skipped, the loop
> ended, and the terminal chunk went out: **an empty answer marked `done: true`** — the fourth "200
> that looks finished" this track has met and the first one it shipped. Found by running the
> published image, not by reasoning. `GeminiUpstreamClient.NotSse` now throws when a streaming
> response contained no `data:` line at all, and it keeps the body (bounded at 64 KB) so the two
> real cases arrive with their reasons: a **block delivered before the stream starts** still reports
> its `blockReason`, and a **JSON array** says that something between here and the vendor dropped
> the query string. D7 caught this shape as a *frame* and had nothing to say about the same body
> unframed.
Three upstreams, three end-of-stream conventions, and still no shared frame reader — 63 D6's reason
unchanged. **Considered and rejected: exposing `alt` as configuration**, which has one legal value
and one that hangs.

**D4 — Streaming usage is cumulative, so counts are taken and never summed — the second vendor in a
row, which makes it a rule.** Every chunk carries the whole `usageMetadata` as it stands. OpenAI
sends one terminal usage chunk; Anthropic and Gemini both publish cumulative. The house rule is now
**read a provider's usage as a snapshot unless it documents an increment**, and never sum without
evidence. A stream with no usage yields no counts, not zeros (v3.13.1).

**D5 — `promptTokenCount` already contains the cached tokens and is passed through whole — the
exact opposite of 63 D5, which is why both are written down.** Gemini documents the prompt count as
*including* cached content, with `cachedContentTokenCount` as a **breakdown** of it; Anthropic's
`cache_read_input_tokens` is a **sibling** of `input_tokens`. So the identical-looking arithmetic is
wrong in opposite directions: adding Gemini's double-counts, adding Anthropic's invents a total.
**Both dialects report what the vendor calls the prompt and neither composes it** — that is the
durable rule, and these two cache fields are the evidence that "just add them up" is not one.
**Considered and rejected: subtracting `cachedContentTokenCount` so the vendors look comparable** —
a hub that adjusts one vendor's count to resemble another's has stopped reporting (25 D3).

**D6 — Thinking tokens are counted, billed as output, and still not folded into `eval_count`; the
lever is configuration, not arithmetic.** Gemini thinks by default. `candidatesTokenCount` is the
answer's tokens, `thoughtsTokenCount` is separate and priced as output, `totalTokenCount` is
prompt + thoughts + candidates. `eval_count` carries `candidatesTokenCount` alone, because a client
reading that field reads *"tokens in the answer I received"* and thinking tokens are not in the
answer. The gap is documented rather than hidden, and `ThinkingBudget` is the operator's lever
(absent = the vendor's dynamic default, `0` = off where allowed). **Considered and rejected:
`candidates + thoughts`** — it matches the invoice and breaks every reader of the field, including
tokens-per-second. **Also rejected: dropping the number silently**, which is how a bill becomes a
mystery. *Related restraint:* 63 D2's `MaxTokens` is **not** reused — Gemini does not require
`maxOutputTokens`, so a declared default would be a ceiling nobody asked for; it travels only when a
caller sets `num_predict`.

**D7 — A blocked prompt is a 200 with no candidates, and it becomes an error.**
`promptFeedback.blockReason` with an empty `candidates` array. **The third "200 that looks finished"
this track has found** — 62 D4's mid-stream `error` frame, 63's `overloaded_error`, now this — and
the third time the answer is the same: throw, name the reason, let the hub write a terminal error
chunk. An empty string with `done: true` is a wrong answer shaped like a right one. A *candidate*
that stops on `SAFETY` after producing text is a different thing and keeps its text: `MAX_TOKENS`
becomes Ollama's `length`, everything else `stop`, and no vocabulary is invented for a client that
has none.

**D8 — Gemini has embeddings, so this is the first dialect whose `EmbedAsync` is real — and it sends
no `taskType`.** `:batchEmbedContents` for one input as well as forty (one code path beats a saved
field). Google's guidance is that `RETRIEVAL_DOCUMENT` and `RETRIEVAL_QUERY` beat the default, and
**this hub cannot tell which it is looking at**: Ollama's `/api/embed` has no such field and phase
44's pipeline calls one model for both ingestion and search. **Considered and rejected: defaulting
to `RETRIEVAL_DOCUMENT`**, which silently degrades every query embedding. **Also rejected: a config
knob** — one value per provider is wrong for half the traffic of any deployment that both ingests
and searches. 63 D7's refusal and this are the same seam answered by two vendors, which is 40 D1's
point a fifth time.

> **Nothing on the coordinator reaches it yet, and v3.32.0's docs briefly implied otherwise.** An
> embedding request goes to `EmbeddingDispatcher` and the fleet, never to `ProviderDispatcher`, so
> mapping an embedding model to a Gemini provider today earns the fleet's 404 — *exactly* what 63 D7
> said about Anthropic's 501, which should have been read as applying to both. The implementation is
> written for **67**. Corrected in v3.32.1, in the README, on the site and here; caught by running
> the published image.

**D9 — `error.code` is a number here, and `status` plus a `RetryInfo` delay are carried through.**
The envelope is `{"error":{"code":429,"message":…,"status":"RESOURCE_EXHAUSTED","details":[…]}}`.
The numeric `code` is the shape that broke the OpenAI dialect for eight releases until 62 D3 found
it, so it is typed as a number from the first line rather than discovered twice. `status` is the
half an HTTP number does not give you and `details[].RetryInfo.retryDelay` is the half a human
needs — Gemini's equivalent of the `request_id` 63 carried, and the same treatment.

*The base64 magic-byte sniff has a third caller* (`inlineData.mimeType`), which is what 63's move to
`Upstream/Base64MediaType.cs` was for. **Nothing is dropped from the sampling options**, unlike 63
D4 — Gemini's `generationConfig` has `seed`, `presencePenalty` and `frequencyPenalty`, so that drop
was a fact about Anthropic rather than a policy here. **Not translated, deliberately:** tools,
`responseSchema`, thought summaries (`includeThoughts` is never sent — a thought part arriving is
skipped anyway), caching and the file APIs. **No live provider was called by any test** — every
payload in `GeminiDialectTests` is recorded (track D6); the first real key is phase 68's.
