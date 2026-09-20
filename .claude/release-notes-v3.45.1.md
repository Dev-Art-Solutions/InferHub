# InferHub v3.45.1 — `Retrieval:Rerank=cross-encoder` crashed the node it was configured on

Found within hours of v3.45.0, by doing the one check that release's own notes said was still
missing: pulling the published `:tools` image and running it for real.

## What was wrong

Both options validators — `VectorStoreOptionsValidator` on the coordinator and
`NodeConfigurationValidation`'s retrieval check on the node — still hard-refused any
`Retrieval:Rerank` value other than `"none"` or `"llm"`. v3.45.0 added the `"cross-encoder"` value
everywhere else (`RetrievalPipeline`, the DI composition roots, the docs), but not in the one place
that runs before any of it: `ValidateOnStart`. A node or coordinator configured exactly as the
v3.45.0 release notes and docs instructed — `Retrieval:Rerank=cross-encoder` — refused to start at
all:

```
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException:
LocalApi:Retrieval:Retrieval:Rerank must be 'none' or 'llm' (got 'cross-encoder').
```

Every other verification in v3.45.0 exercised the reranker's own code paths — the .NET dispatch
logic, the worker's process protocol — but none of it went through `ValidateOptions`, because none
of the unit tests construct a node or coordinator host end to end. Pulling the actual published
`ghcr.io/dev-art-solutions/inferhub-node:tools` image and running it with the documented config was
the first thing that ever did.

## The fix

Both validators now accept `"none"`, `"llm"` or `"cross-encoder"`, with a regression test covering
all three plus the rejection case (`VectorStoreOptionsValidatorTests.EnabledStoreAcceptsAllSupportedRerankModes`/
`EnabledStoreRejectsUnknownRerankMode`).

## Re-verified against the published image

Same container, same config, after the fix:

```
$ curl -X POST http://localhost:5081/api/tools/rerank \
    -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
    -d '{"model":"ms-marco-minilm-l6","query":"how much annual leave do employees get?",
         "documents":["Employees accrue 25 days of annual leave each year.",
                       "The office kitchen is on the third floor.",
                       "Annual leave requests must be submitted two weeks in advance."]}'
{"scores":[8.398737907409668,-11.266807556152344,-3.09686017036438]}
```

Same scores as the standalone worker-protocol check in v3.45.0 — deterministic, correctly ranked —
now reached over real HTTP through `/api/tools/rerank` on the actual published container, with
`Retrieval:Rerank=cross-encoder` set as the node's own startup config rather than left unset. A
request naming a model the reranker doesn't serve gets the expected `503` naming the capability,
through the same real path.

## What is still not established

A full ingest → hybrid search → reranked-order comparison against `Rerank=none`, through the
retrieval pipeline itself rather than the tool endpoint directly, and a run through a real
coordinator + node pair (this was solo mode). Both are next.

## Verification

`dotnet test InferHub.sln` — full regression green: `InferHub.Tests.Coordinator` 769/769 (up from
v3.45.0's 765 — the 4 new validator cases), `InferHub.Tests.Node` 189/189, `InferHub.Tests.Mesh`
441/441, `InferHub.Tests.Shared` 193/193.

**Published-image check:** done — see above. This is the fix that check found.
