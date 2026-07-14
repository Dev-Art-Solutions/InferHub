# Social copy — InferHub v2.5.0 (phase 23, document ingestion)

Blog post: https://devart.solutions/blog/inferhub-2-5-document-ingestion
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v2.5.0

---

## Facebook

> InferHub 2.5 is out — the vector store can finally be filled.
>
> Upload a document to a collection; the coordinator extracts the text, chunks it, embeds it on your own GPU fleet, and stores it. Any request with a retrieve header comes back grounded, with citations down to the page. Text, Markdown, HTML, JSON, PDF.
>
> Three things we deliberately did not do:
>
> — We don't keep your file. Chunks and a hash, nothing else. A retrieval system that quietly becomes a document store has two sources of truth and a data-retention question its owner never agreed to answer.
>
> — We don't silently duplicate. Re-uploading a document replaces it; re-uploading identical bytes does nothing and says so.
>
> — We don't do OCR. A scanned PDF is rejected with an error, not turned into a corpus of confident gibberish. This is the one we'd defend hardest: a bad extraction doesn't fail loudly. It succeeds, quietly, and surfaces months later as a model that is subtly, unaccountably wrong.
>
> Self-hosted, MIT, .NET 10. One new dependency, and it only loads if you upload a PDF.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

---

## X

> InferHub 2.5: document ingestion.
>
> Upload a PDF → text extracted, chunked, embedded on your own GPUs, stored, citable down to the page. Works through any OpenAI client via one header.
>
> No OCR. A scan gets rejected, not turned into gibberish that retrieves confidently.
>
> https://github.com/Dev-Art-Solutions/InferHub

---

## r/LocalLLaMA (if posting)

Lead with the honest bit, not the feature list. The audience there has all been burned by a
RAG pipeline that silently ingested a scan and then confidently answered from nothing.

> **InferHub 2.5 — document ingestion, and a deliberate refusal to do OCR**
>
> InferHub is a self-hosted inference mesh: a coordinator on a cheap always-on box, nodes on your
> GPU machines that dial *out* (no port forwarding), an OpenAI-compatible `/v1` on the front.
>
> 2.5 adds the ingestion side of RAG. Upload text/Markdown/HTML/JSON/PDF; the coordinator extracts,
> chunks, embeds the chunks **on the fleet** (not on the coordinator), and stores them. Retrieval
> then cites the document and the page.
>
> The decision I'd actually like feedback on: **there is no OCR and there won't be.** A PDF whose
> text layer is near-empty is rejected with an error saying it looks like a scan. It would have
> been easy to bolt on an OCR pass that usually works — but a bad extraction doesn't fail. It
> succeeds, quietly, and fills the corpus with near-gibberish that retrieves plausible nonsense.
> You find out months later, as a model that's subtly wrong in ways nobody can trace. I'd rather
> refuse the file and make the owner make that call deliberately, with a tool they chose.
>
> Also: the original file is never kept (chunks + a hash), re-ingest is idempotent and sweeps
> orphaned chunks when a revision is shorter, and a partial ingest returns a 500 with the chunk
> counts rather than a 200 that lies.
