# InferHub v2.11.0 — social copy

**Status: written, NOT posted.** No FB/X connector — the standing debt across every release since v2.4.
Post by hand.

---

## Facebook

> InferHub 2.11 sees images.
>
> The OpenAI-compatible `/v1` surface now forwards vision content to your vision models —
> llava, llama3.2-vision, qwen2-vl, moondream. Send a base64 image the way the OpenAI SDK
> already does and it reaches whichever GPU in your fleet can read it. Blocking and
> streaming, and it works through a vLLM or hosted node too.
>
> Three things it deliberately refuses to do. It won't fetch a remote `http` image URL —
> that would make the coordinator issue outbound requests to caller-supplied hosts, and pull
> third-party bytes through a hop whose whole design is to retain nothing. It won't forward
> base64 it hasn't validated, because a node rejecting it seconds later from behind a GPU
> queue is a much worse error. And it won't guess a media type it can't recognise: labelling
> a non-PNG as `image/png` turns a detectable error into a slightly bad model answer, and
> those are the expensive ones — they don't look like bugs.
>
> The suite was green, so the feature went in front of a real GPU node anyway. Vision worked
> first try. What the run exposed was the error a vision user hits *first* — point the request
> at a text-only model and you got the useful sentence buried under three layers of JSON
> escaping, because Ollama encodes its backend's error as a string inside its own error field
> and our envelope added a third. An SDK read `error.message` and showed a wall of backslashes.
> That had been true for every node error since 2.3. Nobody had read one until vision gave
> them a reason to.
>
> Nothing about the image is stored or logged, usage stays counts-only, and text-only requests
> are byte-identical to 2.10. Still zero new dependencies.
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

### X — alternate (the honest-bug angle, matching the blog post)

> InferHub v2.11 ships vision on the OpenAI-compatible API.
>
> The tests were green, so we ran it against a real GPU anyway. Vision worked. The *error* did
> not: point a vision request at a text-only model and the one useful sentence came back buried
> under three layers of JSON escaping. Broken since 2.3 — nobody had read one until now.
>
> https://github.com/Dev-Art-Solutions/InferHub
