# InferHub v2.11.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
Post by hand.

---

## Facebook

> InferHub 2.11 sees images.
>
> The OpenAI-compatible `/v1` surface now forwards vision content to your vision models —
> llava, llama3.2-vision, qwen2-vl, moondream. Send a base64 image the way the OpenAI SDK
> already does and it reaches whichever GPU in the fleet can read it. It works through an
> OpenAI-backed node too (vLLM, LM Studio, a hosted provider): the image survives the double
> translation, with its media type sniffed from the bytes rather than guessed.
>
> Three things it deliberately refuses to do. It won't fetch a remote `http` image URL — that
> would make the coordinator issue outbound requests to caller-supplied hosts, and pull
> third-party bytes through a hop whose whole design is to retain nothing. It won't forward
> base64 it hasn't validated, because a node rejecting it seconds later from behind a GPU
> queue is a much worse error. And it won't guess a media type it can't recognise: labelling
> a non-PNG as `image/png` turns a detectable error into a bad model answer.
>
> Nothing about the image is stored or logged, and usage stays counts-only — an image
> contributes to the prompt tokens the node reports; we don't measure pixels. Text-only
> requests are byte-identical to 2.10, and it's still zero new dependencies.
>
> Self-hosted, MIT, .NET 10.
> 👉 https://github.com/Dev-Art-Solutions/InferHub

## X

> InferHub v2.11: vision passthrough on `/v1`.
>
> Base64 images now reach your vision models through the mesh — including via vLLM/hosted
> nodes. No remote fetching, nothing retained, media type sniffed not guessed.
>
> Zero new deps, as always.
> https://github.com/Dev-Art-Solutions/InferHub
