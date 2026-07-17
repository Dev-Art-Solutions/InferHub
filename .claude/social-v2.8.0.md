# InferHub v2.8.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
r/selfhosted and r/LocalLLaMA drafts included; post by hand.

---

## Facebook

> InferHub 2.8 is out — two unglamorous features that decide whether a GPU mesh is actually pleasant to run.
>
> First: the hub can finally manage models on the fleet. Pull, delete, warm — from the console, with a progress bar, over the connection the node already holds open. No inbound port on your GPU box; that was never negotiable. `ensure?replicas=3` puts a model on three suitable nodes and tells you why it chose them.
>
> Second: the router has been quietly lying since 1.0. It picked the "least busy" node, meaning fewest jobs in flight — a fine proxy on a uniform fleet, and InferHub's whole premise is that your fleet is not uniform. A 4090 and a laptop with an eGPU both report one job in flight. They will not finish at the same time.
>
> Nodes now carry measured tokens/sec, computed from numbers every job was already reporting. Opt-in this release; we'll move the default when there's evidence, not an argument.
>
> Self-hosted, MIT, .NET 10. Zero new dependencies.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub 2.8:
>
> — Pull/delete/warm models on any node from the console. No inbound port on your GPU box.
> — The router now knows a 4090 isn't a laptop. Measured tokens/sec, not "fewest jobs in flight".
>
> Unmeasured nodes are treated as average, not slow — otherwise they never earn a measurement.
>
> https://github.com/Dev-Art-Solutions/InferHub

## r/selfhosted / r/LocalLLaMA (draft)

**Title:** InferHub 2.8: manage models across a self-hosted GPU fleet from one place, and route by measured throughput

Body: InferHub is a self-hosted, Ollama/OpenAI-compatible inference mesh in .NET — a coordinator on an
always-on box, nodes on your GPU machines that dial out (no port forwarding). 2.8 adds two things a
multi-machine setup actually needs: (1) pull/delete/warm models on any node from the console/API over the
node's existing outbound connection — no SSH, no inbound port — plus `ensure?replicas=N` and a model×node
matrix; (2) opt-in throughput-aware routing (measured tokens/sec EWMA) so a fast card and a slow one stop
being treated as equal, while an unmeasured node is treated as average rather than frozen out. Defaults
unchanged. MIT, zero new deps. Honest answer to "why not LiteLLM": LiteLLM proxies *providers*; this is a
mesh over machines *you own behind NAT* — pointless at one machine, worth it at two.
