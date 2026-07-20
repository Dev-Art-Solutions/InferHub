# InferHub v2.11.0 — Multimodal (vision) passthrough

The OpenAI surface rejected image content parts outright: `RequestTranslator.ExtractContent`
threw on any non-text part. That was the right call at the time — a dropped image is worse than
a clean error — but it locked out the whole vision-model class (`llava`, `llama3.2-vision`,
`qwen2-vl`, `moondream`) that Ollama already serves. The node protocol is Ollama-shaped, and
Ollama takes images as a base64 array on the message, so this is a boundary translation like
every other dialect mapping. Images now reach the GPU that can read them.

## What changed

- `ExtractContent` no longer returns a joined string; it returns a `MessageContent(Text, Images)`
  pair, because OpenAI carries text and images as **one** array of parts and Ollama carries them
  as **two** fields. `image_url` parts become Ollama's base64 `images` array; text parts still
  join. Array order is preserved for multi-image messages.
- `UpstreamTranslator` does the round-trip the other way: a node driving an OpenAI upstream
  (vLLM, LM Studio, TGI, a hosted provider) re-emits `images` as `image_url` data-URL content
  parts, so a vision request that arrived on `/v1` survives the double translation.
- A text-only message still goes upstream as a plain `content` string. Nothing about the
  pre-vision wire changed for requests that have no images.

## Three deliberate refusals

**Remote image URLs are rejected, with a reason.** An `http(s)` `image_url` returns a `400`
saying the coordinator does not fetch remote images. Fetching a caller-supplied URL would make
the hub issue outbound requests to arbitrary hosts — an SSRF surface — and would pull
third-party bytes through a hop whose whole design is to retain nothing (rule 7). Inlining a
hosted image is one line in every OpenAI SDK.

**Malformed base64 fails at the edge, not behind the GPU queue.** The data URL's payload is
validated in the translator. A node rejecting bad base64 seconds later, after routing and a
queue wait, is a far worse error than a `400` here.

**An unrecognised image format fails clean rather than mislabelled.** Ollama's `images` are
bare base64 with no media type, but an OpenAI data URL needs one — so it is sniffed from the
magic bytes (PNG/JPEG/GIF/WebP). Defaulting to `image/png` for bytes that are not a PNG would
produce a bad model answer instead of an error, which is exactly the class of quiet wrongness
this codebase spends errors to avoid.

Audio and video content parts are still rejected, unchanged.

## What was not built

**No capability registry.** A text-only model handed an image errors at the node, and that
refusal is forwarded as-is (a clean `502` carrying the model's own message, never a `500`).
Ollama is the source of truth for what a model accepts; a second list of "which models see
images" maintained here would drift and start lying.

## What the live run found

Verified end-to-end against a real `llava` node — a red circle, correctly described, blocking
and streaming — plus every rejection path. That run turned up an unrelated wart, in the error
a vision user hits *first*: point a vision request at a text-only model and the reply was

```
{"error":"{\"error\":{\"code\":400,\"message\":\"Multimodal data provided, but model
does not support multimodal requests.\",\"type\":\"invalid_request_error\"}}"}
```

Ollama encodes its own backend's JSON error as a **string** inside its `error` field, so the
refusal arrives double-encoded and our envelope made three layers. An SDK reads `error.message`
and shows the user a wall of backslashes instead of the one sentence that says what to fix —
exactly the "useless unknown error" the OpenAI envelope exists to prevent. It now reads:

```
{"error":{"message":"Multimodal data provided, but model does not support multimodal requests.", …}}
```

`InferenceCore.ReadableNodeError` drills to the innermost message, bounded at four levels, and
sits in the one dispatch path so the Ollama surface gets it too. It **unwraps but never
infers**: nothing is decided from the error text and the status is untouched — a function that
started deciding what an upstream error *means* would be the capability registry above, smuggled
in through the back door.

This was not vision-specific and had been true for every node error since phase 21. It shipped
here because vision is what made anyone read one.

## Invariants

- Rule 6 holds: the node-facing job protocol is still Ollama-shaped, images included.
- Rule 7 holds: images pass through in flight in the request body, same as text. Nothing is
  stored, nothing is logged. Usage stays counts-only — an image contributes to the prompt
  tokens the node itself reports; we do not measure pixels.
- Rule 5 holds: **zero new dependencies**. `System.Text.Json` and `Convert.TryFromBase64String`
  do all of it.
- Text-only requests are byte-identical to v2.10.

536 tests green. Verified live against a real llava node (blocking + streaming), not just from source.
