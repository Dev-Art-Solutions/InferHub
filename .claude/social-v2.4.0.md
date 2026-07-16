# Social copy — v2.4.0

Ready to post. Not posted by the release run — post these by hand.

## Facebook

> InferHub 2.4 is out.
>
> A node used to mean Ollama. It can now mean vLLM — or llama.cpp, or LM Studio, or TGI, or a hosted provider. One backend implementation covered all of them, because they all speak the same dialect and 2.3 had already taught InferHub to translate it. For anyone serving more than a couple of users off one GPU, vLLM's continuous batching is the difference between a demo and a deployment.
>
> The second half is the failure nobody plans for: the GPU machine is switched off. InferHub can now fall back to a configured upstream instead of returning a flat 404 — off by default, only for models you explicitly map, tagged in the response header and counted on the status page, and never stored. Your data stays on your GPUs while they're awake; you decide in advance what happens when they aren't.
>
> Self-hosted, MIT, .NET 10. No new dependencies in this release.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub 2.4: nodes can now run vLLM.
>
> One OpenAI-compatible backend → vLLM, llama.cpp, LM Studio, TGI, hosted providers. Continuous batching in a self-hosted mesh.
>
> Plus cloud burst: GPU asleep? Fall back to an upstream — off by default, always tagged, never stored.
>
> https://github.com/Dev-Art-Solutions/InferHub

## Links

- Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v2.4.0
- Blog: https://devart.solutions/blog/inferhub-2-4-openai-backends-and-cloud-burst
- Docs: https://inferhub.devart.solutions
