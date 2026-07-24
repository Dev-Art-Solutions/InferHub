# Social — InferHub v3.2.0 (phase 34, Qdrant-native hybrid search)

Never auto-posted — no FB/X connector; Iliya posts by hand.

## Facebook

InferHub 3.2: hybrid search now runs inside Qdrant.

Last release we said keyword search on Qdrant was coarse. Now a hybrid query fuses a dense embedding and a sparse lexical vector server-side, in one round trip, using Qdrant's own fusion — ranking by meaning and exact terms at once.

The sparse vector is computed on the hub from the same tokenizer our local BM25 index uses, so it added no dependency and no extra model. Still zero new deps since 2.3.

Default retrieval is unchanged; collections from 3.1 keep working. Eval numbers are in the release notes.
👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.2.0

## X

InferHub 3.2: server-side hybrid search on Qdrant.

One round trip fuses a dense + a sparse vector inside Qdrant (RRF), ranking by meaning and exact terms at once. Sparse vector computed hub-side from our existing tokenizer — zero new deps.

https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.2.0
