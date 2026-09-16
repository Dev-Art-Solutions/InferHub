# Social copy — v3.40.0

**Post link:** https://blog.devart.solutions/blog/inferhub-3-40-federated-retrieval
(slug `inferhub-3-40-federated-retrieval`, ID `6aaa1b717cf000d94040e363`, EN-visible / BG-hidden.)

## X — the product angle (243 chars; the link counts as 23)

> InferHub retrieval used to answer exactly one collection per request. Own three corpora across
> two nodes and a hub, and you queried each one separately and merged the results yourself.
>
> v3.40 fans out to all of them in one call and fuses the ranking.
>
> [link]

## X — the honesty angle (255 chars), the most distinctive of the three

> Cosine distance from one vector engine and BM25 from another don't live on the same scale — no
> constant reconciles them.
>
> v3.40's federated search ranks by Reciprocal Rank Fusion instead of pretending the scores
> compare. Same trick hybrid search already used for one collection.
>
> [link]

## X — the zero-new-path angle (238 chars), for people who build retrieval systems themselves

> Federated search across collections sounds like new plumbing: new dispatch, new auth checks,
> new failure modes.
>
> It's a caller of the single-collection search path this mesh already had. Same code, run N
> times in parallel, fused.
>
> [link]

*Counts include the URL as 23. No backticks: X renders them literally.*

## Facebook / LinkedIn

> **InferHub v3.40 — one query, several corpora, and an honest partial answer.**
>
> Retrieval here has always answered exactly one collection per request. Assign a corpus to a
> node and the hub dispatches to its owner; keep one on the hub and it goes through the hub's own
> store. Either way: one name in, one ranked list out. Want an answer that could live in any of
> several corpora — a docs collection here, release notes there, a third one on a different node
> — and you issued one request per name and merged the results by hand, with no principled way to
> rank a cosine score from one engine against a BM25 score from another.
>
> A request body, against the new route:
>
> ```
> POST /api/retrieve/federated
> { "collections": ["site-sofia-docs", "release-notes"], "query": "how does node ownership work", "k": 5 }
> ```
>
> **Every name is answered by the exact code that already answers it.** Hub-owned goes through
> the retrieval pipeline; node-owned is dispatched to its owner, exactly as collection ownership
> already routes it. Both run in parallel, each bounded by its own timeout. A federated result for
> any one name is provably what that name's own single-collection search would have returned, in
> isolation — federation is a caller of that path, not a second one.
>
> The ranked lists come back fused by Reciprocal Rank Fusion — the same technique this project's
> hybrid search already uses to combine a vector branch with a keyword branch, because two
> engines' scores live on scales no fixed constant reconciles. The one thing that couldn't be
> reused as-is: fusion now keys on **(collection, id)** rather than the bare record id, because two
> unrelated collections' chunk `"1"` are different records — a bare-id fuse would have silently
> merged them into one scored entry.
>
> A partial answer says so, per source:
>
> ```
> "sources": [
>   { "collection": "site-sofia-docs", "status": "ok", "matches": 3, "elapsedMs": 42 },
>   { "collection": "release-notes", "status": "timeout", "matches": 0, "elapsedMs": 4000 }
> ]
> ```
>
> A collection outside the caller's scope reports `not_found` — indistinguishable from one that
> genuinely doesn't exist, the same tenancy rule every other collection-naming path here already
> keeps. An owner that's offline reports `unavailable`, never a quiet answer from a different
> corpus of the same name. A slow source times out on its own budget without failing the
> collections that did answer — a federated query that could fail over one sleepy node would be
> strictly worse than issuing the single-collection requests by hand.
>
> **What wasn't established:** no load test against a real fleet under concurrent federated
> traffic, and no de-duplication of identical content across collections — two corpora holding the
> same document produce two entries, named as a decision rather than an oversight.
>
> Zero new dependencies. Full write-up: [link]

## Notes

- **No image.** The honest visual would be a console panel for the new endpoint, and there isn't
  one yet — this phase is API-only. Don't post a mockup of a UI that doesn't exist.
- Lead with the zero-new-path angle on X if there's only room for one — it's the most
  InferHub-specific claim (reusing the single-collection path rather than inventing a second one)
  and the one a technical audience stops scrolling for.
- If the FB copy needs shortening, cut the "What wasn't established" paragraph last — it's the
  credibility anchor, not filler.
