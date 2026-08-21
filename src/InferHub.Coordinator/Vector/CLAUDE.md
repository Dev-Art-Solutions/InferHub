# InferHub.Coordinator/Vector — agent context

**Scope: `src/InferHub.Coordinator/Vector/`.** The three vector providers, the replication and
healing that only one of them has, collection ownership, and the migration path between them.

> **Read the root `CLAUDE.md` first**, and `src/InferHub.Coordinator/CLAUDE.md` for everything the
> hub does that is not retrieval. **Rule 4 is this subtree's whole character**: the vector store is
> the one exception to "no persisted state", and phase 44 sharpened it to *one authority per
> collection name, and the hub knows who it is*.

**Split out of `src/InferHub.Coordinator/CLAUDE.md` in phase 62**, which needed the room and had no
business shortening a record to get it (52 D1's axis: the loader keys on the directory tree, and an
agent working on a provider dialect should not pay for the Qdrant connector's UUID mapping). Nothing
here was rewritten in the move — the blocks came across whole, and
`EveryPhaseDecisionBlockSurvivesTheSplitExactlyOnce` is what proves it.

## Related context

- The stores themselves, and the pure retrieval core: `src/InferHub.Shared/CLAUDE.md` (phases 24, 33, 34)
- The endpoints, the console and the cloud providers: `src/InferHub.Coordinator/CLAUDE.md`
- A node that holds an assigned corpus: `src/InferHub.Node/CLAUDE.md`

## Vector providers

`IVectorStore` ([Vector/IVectorStore.cs](src/InferHub.Shared/Vector/IVectorStore.cs)) is
the seam; three implementations sit behind it, selected by `VectorStore:Provider` and wired in
[Vector/VectorStoreServiceCollectionExtensions.cs](src/InferHub.Coordinator/Vector/VectorStoreServiceCollectionExtensions.cs)
(the single composition root — `Program.cs` and the DI-shape test both go through it):

- **`LocalVectorStore`** — raw store on disk + in-memory `FlatIndex`, replicated to nodes.
- **`PostgresVectorStore`** ([Vector/Postgres/](src/InferHub.Coordinator/Vector/Postgres/)) —
  table-per-collection over pgvector; publishes the two lifecycle events itself and returns the
  same score sign-conventions as `FlatIndex` (see `PostgresSchema.ScoreExpression`).
- **`QdrantVectorStore`** ([Vector/Qdrant/](src/InferHub.Shared/Vector/Qdrant/), phase 33, **moved to
  `InferHub.Shared` in phase 44 D2** so a node assigned a Qdrant corpus runs the same store rather
  than a second one — only `QdrantBootstrapper` stayed behind, as a host concern) —
  Qdrant over a hand-rolled REST `QdrantClient` (no dependency); publishes its own lifecycle
  events and returns the same `FlatIndex` score conventions (Qdrant reports them with no sign flip).
  Since phase 34 it also fuses dense + sparse **server-side** (`IServerSideHybridSearch`) for
  collections created on 3.2+; collections created on 3.1 stay dense-only until migrated. Phase 35
  added collection-creation knobs (quantization, on-disk vectors, payload indexing).

Since phase 35 there is a **migration path between providers**: `tools/InferHub.Migrate` copies a
populated collection from any provider to any other, over `ScanWithVectorsAsync` → `UpsertAsync`. It
is an operator tool, outside the runtime — see "Phase 35" below before changing anything about it.

`postgres` and `qdrant` are **external providers** — `VectorStoreProviderExtensions.IsExternal` is
the one predicate every call site (`Program.cs`, `StatusEndpoint`, `VectorEndpoints`) branches on,
because what matters is external-vs-local, not which external one. Do not reintroduce per-provider
`IsPostgres` checks at those sites.

Hard rule: **`ReplicationCoordinator` and `HealingService` bind to `LocalVectorStore`
concretely** (they subscribe to its `CollectionCreated` / `RecordUpserted` events). Do **not**
widen them to `IVectorStore` — that would drag replication concerns into the interface. Under any
external provider they are simply not registered; `VectorCompositionTests` fails if anyone
re-couples them. That's why the external stores publish `vector.collection.created` / `.dropped`
themselves, and why `NullVectorQueryRouter` replaces the node-serving router.


## Decisions recorded here

### Phase 31 (client-scoped collections) — also load-bearing

**D1 — Scoping is an authorization filter over the one vector store, not a store per tenant.**
`Auth:Clients[].Collections` (null/absent = all, exactly as before v2.13) is answered in exactly one
place, [CollectionAccessPolicy](src/InferHub.Coordinator/Auth/CollectionAccessPolicy.cs) — the same
"one place saturation is defined" discipline as `FleetSaturation`. Rule 4 survives untouched: there
is still one collection namespace and one source of truth, and a scope only decides which names a
key may say. Only a **trailing `*`** is a wildcard; a full glob dialect or a regex in a config file
is a footgun aimed at an isolation boundary, and `tenant-a-*` is what provisioning actually needs.

**D2 — Enforcement is a group filter, not a line in each handler.** `RequireCollectionScope()` hangs
off the route groups that carry `{collection}` (`/api/collections/{c}/documents`, the search route,
the `/api/vector/{c}` data plane). The ingestion group alone has five routes, and **the one that
gets forgotten is the isolation hole** — so no path may enforce this inline. Inline retrieval is the
one exception, because it names its collection in a *header*: the check lives in
`InferenceEndpoints.TryReadRetrievalHeader`, the single parser both client dialects already share.

**D3 — Out of scope is `404`, and the check runs before the store is consulted.** Phase-25 D4's
principle: a tenant is not told another tenant's collections exist. The denial is the *same
sentence* a missing collection produces (`collection 'x' does not exist`) at the same status. And
because nothing is looked up first, a name outside a client's scope reads identically whether or not
it exists — so the 404 leaks nothing, it only reflects the caller's own scope back at them.
`CollectionAccessTests` asserts the real-vs-imaginary pair are indistinguishable.

**D4 — A scoped-out `X-InferHub-Retrieve` is an error, not a passthrough.** It does **not** go
through `Retrieval:OnMissing`: answering without the context the caller asked for, silently, is the
wrong failure on a tenancy boundary. `CollectionNotVisibleException` is caught by both surfaces and
rendered as a 404 in each dialect (`collection_not_found` in the OpenAI envelope).

**D5 — Auto-provision on first ingest, for scoped clients only. This is a deliberate reversal of
half of phase 23's refusal, and only half.** Phase 23 declined to auto-create collections for two
reasons: it would guess the dimension, and it would route around the admin scope that owns
collection lifecycle. The second dissolves for a client whose config *names* its collection scope —
that list **is** the provisioning grant. The first does not dissolve, so nothing is guessed:
creation is deferred until the first batch comes back embedded and the dimension is **measured**
from the vectors (`IngestionPipeline.EmbedAndUpsertBatchAsync`). A consequence worth keeping: an
embed that never succeeds leaves **no** empty collection behind for the next caller to misread as
provisioned. An unscoped client keeps the phase-23 contract exactly — `autoProvision: false`.

**Rule 5 survived again.** Phase 31 added **zero** new dependencies.

### Phase 35 (Qdrant in production + cross-provider migration) — also load-bearing

**D1 — The migration tool is an operator action *outside* the runtime, so rule 4 is untouched.**
[tools/InferHub.Migrate](tools/InferHub.Migrate) is a standalone console (Eval discipline: **not**
built into either image). The coordinator never migrates itself and no second store appears at
runtime — a hub that copied itself into another engine would be a second write path and, for as long
as the copy ran, a second truth. Rule 7 holds: the tool moves chunk text and vectors that are already
in a store and retains nothing beyond the copy it is making.

Unlike Eval it **does** reference `InferHub.Coordinator`, and that is deliberate: it stands up real
stores through `AddInferHubVectorStore` — the one composition root — rather than reimplementing three
connectors. A tool with its own copy of "how a provider is built" is the copy that silently rots. It
starts the provider's `IHostedService`s by hand, which is what creates the schema on an empty
Postgres, warms the Qdrant cache, and fails fast with the coordinator's own message when a store is
unreachable. The reference points one way only; nothing ships inward.

**D2 — `ScanWithVectorsAsync` joined `IVectorStore`, and only because a per-id fetch is not a tool.**
`ScanAsync` deliberately omits the embeddings (phase 23 D1), which is right for finding a document's
chunks and useless for copying them. The alternative was `GetAsync` per record — a round trip per
chunk against stores that answer a page in one, which nobody would run on a million chunks. So the
seam grew a twelfth method, implemented by all three providers with **identical filter, ordering and
`afterId` semantics** to `ScanAsync`, and pinned in `VectorProviderParityTests` /
`QdrantVectorStoreTests`: two providers that disagreed here would give a migration between them a
different corpus on the far side. `ScanAsync` stays the default for everything else.

Qdrant keeps phase-33 D4's discipline here — it scrolls by its own UUID point id, so the filtered set
is materialised and sorted by real id before the window is taken — and unpacks the dense floats from
the **named** vector map a hybrid collection returns. A migration that copied an empty `float[]`
because the wire shape changed would be the quietest possible data loss.

> **Qdrant stores the unit-normalised vector under `Cosine`, and the one you sent under `Dot` and
> `Euclid`.** Found by running the new parity arm against a real Qdrant 1.12.4 — `[0.1, 0.9, 0]` came
> back as `[0.1104…, 0.9938…, 0]`. A stub would have echoed what it was handed and this would have
> shipped unnoticed. It is **safe**: cosine is scale-invariant, so rankings and scores are identical
> either way, and `ACosineRoundTripThroughQdrantKeepsTheRankingAndTheScores` pins exactly that.
> But it is real — a cosine collection migrated *out of* Qdrant carries normalised vectors, so
> anyone diffing raw floats across a migration will see different numbers and must not conclude the
> copy is broken. `ScanWithVectorsMatchesTheLocalStore` therefore asserts the honest thing per
> metric: verbatim under `dot`/`l2`, same direction and unit length under `cosine`. If a future
> provider is added, ask what *it* does to a vector on the way in before assuming a byte-for-byte
> round trip.

**D3 — Quantization / on-disk / payload indexing are collection-*creation* options, and are honest
about the trade.** `Quantization` (`none|scalar|binary`), `OnDisk` and `PayloadIndexKeys` are applied
by `CreateCollectionAsync` and **do not touch existing collections** — which is why migrating is also
how a collection adopts them. Quantization is a *memory-for-recall* trade, not a free win, and the
docs say so with a pointer at the eval harness rather than an adjective: a store that ranks
approximately and is described as "faster" is a store that lies about relevance. `PayloadIndexKeys`
indexes `__meta.<key>` — the same path `BuildFilter` writes; an index on any other path would be
built, reported healthy and never used. A refused index is logged and skipped, not fatal: the
collection works without it, just slower, and losing a collection over an optimisation is the wrong
trade.

**D4 — A remote Qdrant with no API key warns; it does not refuse.** Qdrant ships unauthenticated —
fine on localhost, a data leak anywhere else, since the payload holds the chunk *text*. So
`QdrantBootstrapper` warns at startup when the `Url` is non-loopback and `ApiKey` is empty. Not a
hard failure on purpose: a private network with its own controls is a legitimate deployment, and
refusing to boot would be us overruling an operator about their own network. Compare phase-32 D3,
where demoting *was* correct — there the alternative was two hubs both serving, which is a
correctness failure; here the alternative is someone else's risk assessment.

**D5 — The tool refuses a shape mismatch and never deletes.** A target collection that already exists
with a different dimension or distance is **skipped with a reason**, not half-filled: the first fails
per record, and the second would *succeed* and silently rank differently. Records in the target that
are not in the source are left alone — a migration tool that removes data nobody asked it to remove
is a worse failure than one that leaves a stale record behind. Re-running converges rather than
duplicating (deterministic ids, phase 23 D5), so an interrupted run is resumed by running it again.
And the summary reports the **target's own count**, exiting non-zero when it is short: "the upserts
returned" is not the same claim as "the data is there."

**The "no migration path between providers" caveat is deleted, everywhere.** It was true from v2.2 to
v3.2 and was repeated in the README twice, the site and the release notes. A caveat that is no longer
true is as bad as a missing one — if a future change makes it true again, put it back in all of those
places.

**Rule 5 survived again.** Phase 35 added **zero** new dependencies to the shipped projects; the
tool references, and ships nothing inward.

### Phase 44 (hub-assigned retrieval) — also load-bearing

**D1 — Phase-38 D1 is amended precisely, and the invariant it protected is intact. Read this
twice.** The sentence to check any future change against is: **one authority per collection name, and
the hub knows who it is.**

Phase-38 D1 refused to boot a node that was both meshed and holding its own corpus, because such a
node holds hub-derived replicas *and* an authority under the same names, and `ReplicationCoordinator`
would eventually overwrite a collection its operator believed they owned. **That reasoning is not
reversed.** What changed is that the hub can now be the one who *assigns* the corpus, and can
therefore be the one who *knows*:

- [CollectionOwnership](src/InferHub.Coordinator/Vector/CollectionOwnership.cs) records an owner per
  name — `hub` or `node:{id}` — **one place**, the way `CollectionAccessPolicy` and `FleetSaturation`
  are one place. It is re-derived from the profile book on every re-assert and on every node's
  registration pull, so it follows the documents rather than accumulating beside them.
- **A hub-side create of a node-owned name is a `409` naming the owner.** Disjointness is structural,
  not a convention somebody has to remember.
- **Replication and healing bind to it.** `ReplicationCoordinator` skips placement and fan-out for a
  node-owned name; `HealingService` skips it in the sweep and refuses a manual rebuild with the
  owner's name. `VectorCompositionTests` asserts both constructors still take the record — a refactor
  that dropped the parameter would leave every behavioural test passing against a default of
  "everything is the hub's", which is exactly the silent failure this decision is about.
- **A self-configured corpus on a meshed node is still a startup failure.** `LocalRetrievalOptions`
  set by hand plus `Coordinator:Enabled=true` fails exactly as it did in v3.6. The *only* way a meshed
  node holds an authority is that the hub granted it, recorded it, and excluded it from replication.

**D2 — A node runs `local` or `qdrant`, never `postgres` — and phase-33 D2 is why this cost nothing.**
The Qdrant connector was hand-rolled over `HttpClient` with zero dependencies because the official
client is gRPC. That decision, made to protect rule 5 two releases earlier, is what let
`QdrantVectorStore`, `QdrantClient`, `QdrantIdMap` and `SparseVector` **move into `InferHub.Shared`**
— so a node assigned a Qdrant corpus runs the *same* store rather than a second one. The two host
couplings became phase-38 D3's seams (`IVectorLog`, plain options) plus two plain events the
coordinator forwards to `VectorEvents`; `QdrantBootstrapper` stayed behind as a host concern, which
is also why `using InferHub.Coordinator.Vector.Qdrant;` still resolves everywhere it was written.
`PostgresVectorStore` stays on the coordinator, because `Npgsql` is a package and rule 5 names it as
coordinator-scoped — and the node's validator refuses `postgres` **by name**, with that reason,
rather than reporting an unknown value.

**A dependency declined two releases ago is the reason a feature was free today.** `InferHub.Shared.csproj`
is still an empty `<Project Sdk="Microsoft.NET.Sdk">`.

**D3 — The corpus starts and stops at runtime; the routes are always mapped.**
[RetrievalHost](src/InferHub.Node/Retrieval/RetrievalHost.cs) is the **only** thing on a node that
constructs a vector store. DI holds the seams around it and builds nothing until
`StartCorpusAsync` is called, because ASP.NET cannot map an endpoint after the application has
started and a node that restarts itself on a hub instruction is refused by phase-43 D6.

- The retrieval routes are **mapped unconditionally** and answer **501** with the phase-37 D8
  sentence while no corpus runs. `SoloRetrievalTests` records that amendment: they were 404s through
  v3.11. Same shape as tools (41) and audio (42).
- Stopping **drains**: a request already retrieving holds a `CorpusLease` and finishes against the
  store it started on, bounded at 30s.
- **A start that fails leaves no corpus at all** — never a half-started one that answers some
  queries. An `HttpClient` timeout arrives as a `TaskCanceledException`, so the catch distinguishes
  the caller's token from the operation's own timeout; getting that wrong would have turned the most
  ordinary failure (an engine that is slow to refuse) into an exception nobody converts to a refusal.

**D4 — The hub names a credential; the node resolves it. The hub never carries the secret.**
`credentialRef` resolves against `LocalApi:Retrieval:Credentials:{ref}` on the box. An unresolvable
ref is a **refusal naming the key**, never a fall back to an unauthenticated connection. The
alternative — the hub pushing a key down the link — makes the coordinator a secret distributor, puts
credentials in profile persistence and in an admin API response, and hands every node in the selector
a secret it may not need.

**Phase-35 D4 still applies to the node**: a non-loopback engine with no credential **warns and
proceeds**, because that is the operator's own network. The asymmetry with D1's hard refusals is the
line the whole track draws — refuse when the risk is somebody else's, warn when it is theirs.

**D5 — Ingestion and search for a node-owned collection go through the hub, dispatched to the owner.**
The client-facing API stays the hub's; `NodeCorpusDispatcher` sends a `corpus-ingest` /
`corpus-search` job down the connection the node already opened (phase-26 D1 — the hub still never
dials a node), and the node runs the **shared** pipelines against its own store. One API for clients,
no new surface, no second ingestion path, and the hub keeps enforcing phase-31 D2's client scoping
over data it does not hold. An owner that is not connected is a **503 naming the node**, never a
silent fall back to the hub's own store — that would be phase-31 D4's failure, a confident answer
from the wrong data. **PDF is a 415 on a node-owned collection** (phase-38 D5, unchanged): a hub that
extracted the text and shipped chunks would be a second ingestion path with different behaviour.

**D6 — What the hub knows about a node corpus is what the node reported, not what the hub queried.**
`ReportCorpusState` rides the existing model-refresh loop and fires again right after a profile
touches the corpus; `NodeCorpusRegistry` is a mailbox, and `/api/status` reads it. The hub does
**not** query a node's corpus, because that is a synchronous dependency on a box that may be asleep
and `/api/status` has to answer when the fleet does not. A stale block is the honest failure mode,
and the timestamp says so.

**Rule 5 survived again.** Phase 44 added **zero** new dependencies.

