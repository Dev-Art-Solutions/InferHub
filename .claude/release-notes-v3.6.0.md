# InferHub v3.6.0 — RAG on one machine, and the one thing it refuses to do

v3.5 gave a single machine the hub's API: no coordinator, no enrollment secret, no internet, one
process. It stopped one step short. A retrieval header got a clean `501`, because the vector store
lived in the coordinator and dragging it into the node meant dragging a Postgres driver and a PDF
parser with it.

That was the right call for a small release and the wrong place to leave it — the deployment that
most wants RAG is the one with a folder of documents and one GPU, not the fleet. So in 3.6 a
standalone node ingests, indexes and grounds its own answers.

```bash
# ingest
curl localhost:5081/api/collections/handbook/documents \
  -H 'content-type: application/json' \
  -d '{"id":"leave-policy","text":"Employees accrue 25 days of annual leave each year."}'

# see what retrieves, before you trust it
curl localhost:5081/api/collections/handbook/search \
  -H 'content-type: application/json' -d '{"query":"how much annual leave?"}'

# ground an answer
curl localhost:5081/api/chat -H 'X-InferHub-Retrieve: handbook' \
  -H 'content-type: application/json' \
  -d '{"model":"llama3","messages":[{"role":"user","content":"how much annual leave?"}],"stream":false}'
```

Same headers, same augmented prompt, same `X-InferHub-Sources` citations as a coordinator — vector,
keyword and hybrid modes, and the optional LLM reranker. Not "equivalent to": **the same code**.

```jsonc
"Coordinator": { "Enabled": false },
"LocalApi": {
  "Enabled": true,
  "Retrieval": { "Enabled": true, "DefaultEmbeddingModel": "nomic-embed-text" }
}
```

## The interesting part of this release is what it refuses to do

**Retrieval requires `Coordinator:Enabled=false`. Turn it on while the node is in a mesh and it
refuses to start, naming both keys.**

A node in a mesh already holds vector replicas — *derived* copies of the coordinator's collections,
on disk, maintained by the hub pushing down. Give that same process an authoritative corpus of its
own and there are two vector authorities inside one node: a locally ingested document is invisible
to the fleet, a collection name that exists in both places has two different sets of chunks under
it, and the hub's replication will happily overwrite a collection you believe you own. InferHub has
had one rule about this since v1.5 — *one source of truth per deployment, and node replicas are only
ever derived from it* — and there is no configuration of the above that keeps it true.

It refuses rather than quietly switching the feature off, and that is the opposite of what we do
elsewhere: a node whose Ollama supervisor cannot run just says so in one log line and carries on.
The difference is what is at stake. A disabled supervisor costs an operational nicety. Grounding
silently switched off costs you confident, fluent, **ungrounded** answers with no signal at all —
and a bug report three weeks later that begins "the model got worse". An explicit opt-in that cannot
be honoured has to be loud.

## Moved, not copied

Retrieval has a dozen decisions in it — how the two branches fuse, how `k` is clamped, what happens
when a collection is missing, what a failed reranker does, the context template, the stale-chunk
sweep, the rule that decides a document is `partial`. Every one of those quietly diverging between
two hosts produces *plausible answers*, which is the failure nobody notices.

So none of it was reimplemented. The whole pure core — the store, the retrieval pipeline, the
ingestion pipeline, the inverted index, RRF fusion, the contracts — **moved** out of the coordinator
into the shared library, and both hosts now compose the same objects. The coordinator kept what is
genuinely its own: the Postgres and Qdrant providers, replication and self-healing, its endpoints,
its metrics, the PDF extractor. Its own test suite, written for other phases entirely, is what
proved the move changed no behaviour.

The shared library is still a plain class library with **zero package references**, which took some
care: it cannot see `ILogger` or `IOptions<T>` without taking two NuGet packages, so it took neither.
A two-method logging seam and plain options objects were cheaper than ending a streak.

What each host still owns separately is the ten lines that parse the retrieval headers and serialise
the citations — plumbing, not content, the same split we made for SSE framing in 3.5. A parity suite
drives the same corpus and the same question at a real hub and a real solo node over real HTTP and
compares the augmented prompt, the sources header and the ranking in all three modes, because those
are what a client actually sees.

## What a node deliberately cannot do

**No PDF.** The PDF text extractor is the coordinator's one dependency for the job and it stays
there. A PDF upload to a node is a clean `415` telling you to convert the file or use a hub. That is
the same reasoning that made us refuse OCR in 2.5: a bad extraction does not fail, it *succeeds
quietly* and fills a corpus with plausible nonsense that retrieves confidently and surfaces months
later as a model that is subtly, unaccountably wrong.

**No external vector databases.** The local store is the only provider on a node. A deployment that
wants Postgres or Qdrant has outgrown one process.

**No admin API, no console, no `/metrics`.** Unchanged from 3.5. `/api/status` grows a retrieval
block — is it on, which embedding model, which mode, the collections and their record counts — and
still does not invent fleet numbers it has no concept of.

## In a container

```bash
docker run -d --name inferhub-solo \
  -e LocalApi__Enabled=true \
  -e Coordinator__Enabled=false \
  -e LocalApi__Retrieval__Enabled=true \
  -e LocalApi__Retrieval__DefaultEmbeddingModel=nomic-embed-text \
  -e LocalApi__ApiKeys__0=your-key \
  -e Ollama__Endpoint=http://host.docker.internal:11434/ \
  -v inferhub-solo-data:/data \
  -p 5081:8080 ghcr.io/dev-art-solutions/inferhub-node:3.6.0
```

**The volume is the part people forget.** The corpus is written to `/data/retrieval`; without a
volume it dies with the container and every document has to be ingested again.

## Upgrading

Nothing to do. `LocalApi:Retrieval:Enabled` defaults to `false`, and with it off a v3.6.0 node is
byte-identical to v3.5.1: the retrieval header still gets its `501`, the RAG routes still `404`, and
no corpus directory is created. The coordinator gained no behaviour at all — code changed projects,
nothing changed meaning.

Zero new dependencies, for the ninth phase running.
