# InferHub v2.13.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
Post by hand.

Blog post is **live**: https://devart.solutions/blog/inferhub-2-13-scoped-collections
("InferHub 2.13: one mesh, several clients, and a 404 that tells you nothing")

---

## Facebook

> InferHub 2.13 makes the mesh multi-tenant where it actually counts: your RAG collections.
>
> Since 2.7 an API key has had an identity, a budget and a bill. But collections stayed fleet-wide —
> any key that could ingest could reach any corpus. Fine when you're the only owner. Not fine when
> you're an agency running one mesh behind several end-clients.
>
> Now a client key can be scoped to a set of collections (`acme-*`, `globex-docs`, …). Tenant A
> simply cannot see, ingest into, search or retrieve from tenant B's corpus — on any path, including
> the inline-RAG header on both the Ollama and OpenAI surfaces.
>
> And a collection outside your scope returns 404, not 403 — the same answer a collection that
> doesn't exist gives, decided before we ever look in the store. So nobody even learns it's there.
>
> Provisioning is just ingesting: post a document to a new collection inside your scope and it gets
> created, with the dimension measured from the embeddings rather than guessed. No admin round trip
> per tenant.
>
> Still one vector store and one source of truth — this is an authorization filter, not a database
> per customer. A config that never heard of scoping runs exactly as it did in 2.12.
>
> Self-hosted, MIT, .NET 10. Zero new dependencies, as ever.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub v2.13: client-scoped RAG collections.
>
> One mesh, many tenants, zero cross-tenant visibility — an out-of-scope collection returns 404, not
> 403, decided before the store is touched. So nobody learns it exists.
>
> One store, one truth. Zero new deps.
> https://github.com/Dev-Art-Solutions/InferHub
