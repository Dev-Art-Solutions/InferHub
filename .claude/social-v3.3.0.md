# Social copy — v3.3.0

Post by hand (no connector). Blog post is live:
https://devart.solutions/blog/inferhub-3-3-qdrant-in-production-and-provider-migration

## Facebook

> InferHub 3.3: Qdrant for production, and a migration tool that deletes a caveat we'd shipped since 2.2.
>
> There's finally a way to move a populated deployment between backends — a standalone `inferhub-migrate` that copies every chunk and vector from any provider to any other (local ↔ Postgres ↔ Qdrant), safely re-runnable, with a --dry-run. Switching stores no longer means re-ingesting documents we deliberately don't keep.
>
> Plus the knobs a real Qdrant wants: scalar/binary quantization (a memory-for-recall trade, documented as one — measure it with the eval harness rather than trusting an adjective), on-disk vectors, payload indexing, and a startup warning if you point at a remote Qdrant with no API key.
>
> One thing we only found by running it against a live Qdrant: it stores the *normalised* vector in a cosine collection. Harmless — cosine doesn't care about length — but it would have cost someone an afternoon diffing floats after a migration, so it's pinned by a test and written down.
> 👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.3.0

## X

> InferHub 3.3: Qdrant in production + a provider migration tool.
>
> inferhub-migrate copies chunks + vectors between any two backends (local ↔ Postgres ↔ Qdrant), safely re-runnable, --dry-run included. The "no migration path" caveat is gone.
>
> Plus quantization, on-disk vectors, remote-auth warnings. Still zero new deps.
>
> https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.3.0

## X (alternative — leads with the finding)

> Shipped InferHub 3.3 and learned something by running it, not reading docs:
>
> Qdrant stores the *unit-normalised* vector in a cosine collection. Send [0.1, 0.9, 0], read back [0.1104, 0.9938, 0]. Dot and Euclid store what you sent.
>
> Harmless — cosine is scale-invariant, same ranking, same scores. But a stub would have echoed the input and we'd have shipped it blind. It only shows up against the real server.
>
> https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.3.0
