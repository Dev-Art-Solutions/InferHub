# Social copy — InferHub v2.7.0 (phase 25)

Post manually; no connector for FB/X. Blog: https://devart.solutions/blog/inferhub-2-7-quotas-and-usage

## Facebook

> InferHub 2.7 is out: API keys now have names, budgets and bills.
>
> Authentication used to be a flat list of strings — every key anonymous and unlimited. Fine for one person and their own GPUs. Not fine the moment somebody else is holding one.
>
> Keys are now clients: an id, per-key concurrency and rate limits, a daily token budget, a model allowlist. Usage is metered per client and per model and exports as CSV — with an optional append-only Postgres ledger so a restart doesn't erase the invoice. Over the limit gets you a 429 with a Retry-After; over budget gets you a 402. A full fleet queues briefly, with a real bound, and then says 503 — an unbounded queue is a memory leak that shows up as a latency problem three hours later.
>
> One thing we want to be blunt about: a usage record is a client id, a model, two integers and a timestamp. Not the prompt. Not the completion. Not a hash, not a sample. There isn't a flag to turn that on, because a flag is an invitation.
>
> Existing configs run unchanged.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X (276 chars with the t.co link)

> InferHub 2.7: per-key quotas & usage accounting.
>
> Named clients, rate + token budgets, model allowlists. 429 over limit, 402 over budget, bounded queue when the fleet is full.
>
> A usage record is an id, a model, two ints, a timestamp. Never the prompt.
>
> https://github.com/Dev-Art-Solutions/InferHub

## r/selfhosted (if posting)

**InferHub 2.7 — self-hosted OpenAI-compatible GPU mesh now does per-key quotas and usage accounting**

InferHub is a coordinator (no GPU needed) + nodes that dial out from behind NAT and serve
Ollama/vLLM through one OpenAI-compatible endpoint. 2.7 makes it safe to hand keys to other
people: named clients with concurrency/rate/token limits and model allowlists, per-client
usage you could put on an invoice (CSV export, optional Postgres persistence), honest
rejections (429 + Retry-After, 402 over budget), and a bounded queue when every node is at
capacity. The metering stores counts, never text — no prompt, no completion, no hash, and
deliberately no flag to change that. Flat key lists from older configs keep working
unchanged. MIT, .NET 10, zero new dependencies this release.
https://github.com/Dev-Art-Solutions/InferHub
