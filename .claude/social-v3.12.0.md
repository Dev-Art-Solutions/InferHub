# Social — v3.12.0

Post manually. The angle is **not** "you can now put RAG on a node" — that reads like a checkbox and
half the audience already assumes it worked. The angle is the refusal that had to be *kept*:
**a node could never hold its own corpus while meshed, because that is one collection name with two
truths — and 3.12 does not bend that rule, it makes the hub the one who knows who owns what.**

Second-best hook, for the dependency-hygiene crowd: **a gRPC client we declined in 3.1 is the reason
this release was free.** The Qdrant store *moved* into the shared library and ran on a node unchanged.
People who have fought a transitive dependency feel that one.

Third, for the ops crowd, and it is the sentence to lead the X post with if the first two land flat:
**an owner that is not connected is a 503 naming the node, never a quiet answer from the hub's own
store.**

Link to the release.

## Facebook

InferHub 3.12: a corpus on every node, assigned from one place.

We've had retrieval since 2.0, and a standalone node has been able to run its own since 3.6. What we never had is what people actually asked for: a corpus on a node that is *still part of the fleet* — a box in an office, holding that office's documents, answering grounded questions about them.

It took six releases, and the reason is worth more than the feature.

3.6 refused — at startup, loudly — to let a meshed node turn retrieval on. That wasn't caution. A meshed node already holds vector replicas *derived* from its coordinator: the hub is the authority, the node's copies are disposable, and a healing pass rebuilds them whenever they drift. Give that same process an authoritative store of its own and one collection name means two different things. A document ingested locally is invisible to the fleet. A replication pass pushes the hub's idea of that collection over the node's. Nobody is told, because from each side everything looks fine.

**3.12 does not relax that rule. It changes who knows.**

The refusal is still there — a node that switches retrieval on *by hand* while meshed still fails to start, and there's a test in this release whose only job is to keep that true. What's new is the case it couldn't express: a corpus the hub *assigned*, and therefore a corpus the hub has written down.

A node profile grows a `retrieval` section — engine, address, which collections, which embedding model — and every matching node brings the corpus up while it goes on answering chat. No file edited on the machine, no restart, no dropped inference. Switch it off and the retrievals already running finish against the store they started on rather than faulting.

And on the hub, one new fact: every collection name now has an owner, `hub` or `node:{id}`. From that:

• A hub-side create of a node-owned name is a 409 *naming the owner* — not a mystery conflict.
• Replication never targets it. There's nothing hub-side to derive replicas from, so a placement pass over one would push an **empty** snapshot at its owner. That's the hub deleting your corpus while reporting success — the exact failure 3.6 refused to risk.
• Healing never targets it either.

One sentence to check any future change against: **one authority per collection name, and the hub knows who it is.**

The client API doesn't move. Documents still go to the hub, searches still go to the hub, same bodies, same statuses, same scoping and quotas — the hub sees a node-owned collection and dispatches the work to its owner, which runs the *same* pipelines the coordinator runs. Not a smaller reimplementation: the same code, so chunking, deterministic ids, k clamping, fusion and the rerank fallback can't quietly drift between the two places your documents might live.

Two refusals said out loud rather than left to be discovered: PDF on a node-owned collection is a 415 (the extractor ships with the coordinator only, and a hub that extracted text and shipped chunks would be a second ingestion path with a different failure mode). And an owner that isn't connected is a 503 *naming the node* — never a quiet answer from the hub's own store, even when a collection of that name exists there. A confident answer from the wrong corpus is the failure nobody notices for three weeks.

The hub names a credential; it never carries one. `credentialRef` is a *name*, resolved on the node against config that lives on the node, and a ref the box can't resolve is a refusal naming the key — never a quiet fall back to an unauthenticated connection to somebody's vector database. A hub that pushed keys down the link would be a secret distributor.

And the part I like most: **zero new dependencies, because of a decision made two releases ago.** Qdrant's official client is gRPC, so in 3.1 we hand-rolled the connector over HttpClient instead. Which is why, this release, the whole store could simply *move* into the shared library and run on a node unchanged — not ported, not reimplemented smaller. Moved. If one assertion in its test suite had needed editing, it wouldn't have been a move.

994 tests. If you're on 3.11 and assign no corpus, upgrading changes nothing at all.

👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.12.0

## X / Twitter — single post (259/280; the link counts as 23 under t.co)

InferHub 3.12: a corpus on every node, assigned from the hub.

We spent 6 releases refusing this, because a meshed node with its own corpus is one collection name with two truths.

We didn't relax the rule. We made the hub record who owns what.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.12.0

## X / Twitter — the dependency post (single, 251/280)

In 3.1 we declined Qdrant's official gRPC client and hand-rolled the connector over HttpClient.

Two releases later that's why a node can run a Qdrant corpus for free: the store MOVED into the shared library, unchanged.

Dependencies you don't take pay you back.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.12.0

## X / Twitter — thread (each under 280; link only on 5/5)

**1/5** (247)

InferHub 3.12: the coordinator can now assign a node a corpus — engine, address, collections — and the node brings it up while it keeps answering chat.

No restart. No file edited on the box.

The interesting part is the rule we had to keep to ship it.

**2/5** (272)

Since 3.6 a meshed node was refused its own corpus, at startup, loudly.

Not caution: it already holds replicas DERIVED from the hub. Add an authority of its own and one collection name means two things — and a replication pass overwrites one of them while both sides look fine.

**3/5** (258)

3.12 doesn't relax that. It changes who knows.

Every collection name now has an owner: hub, or node:{id}.

Hub-side create of a node-owned name → 409 naming the owner. Replication and healing never touch it — a placement pass would push an EMPTY snapshot at its owner.

**4/5** (269)

The client API doesn't move. Documents and searches still go to the hub; it dispatches them to the owner, which runs the same pipelines the hub runs.

An owner that's offline is a 503 naming the node — never a quiet answer from the hub's own store.

A confident answer from the wrong corpus is the worst kind.

**5/5** (231)

And the hub names a credential; it never carries one. A ref the box can't resolve is a refusal naming the key, not an unauthenticated connection to your vector DB.

994 tests. Zero new dependencies, 9 releases running.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.12.0
