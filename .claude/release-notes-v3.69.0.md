# InferHub v3.69.0 — pictures, voices and transcripts from OpenAI and Google, with an API key

Until now every picture, voice and transcript InferHub served came from a Python worker on a node with a GPU, and the
cloud providers served chat only. This release lets a box with no GPU answer `/v1/images/generations`,
`/v1/images/edits`, `/v1/audio/speech` and `/v1/audio/transcriptions` from **OpenAI** or **Google** — through the same
routes, the same envelopes and the same usage units a GPU worker's answer gets. It works in two places, each with its
own keys: a **coordinator provider's `Media` map**, and a **node's own `CloudMedia` section**.

## What a ChatGPT sign-in can and cannot do here

OpenAI's *Sign in with ChatGPT* (v3.68) lists image generation, audio input and the transcription API as
**unsupported** in its preview limitations. So a ChatGPT sign-in cannot make a picture or a voice, and this release does
not pretend otherwise: those need an API key. What it *can* do is read a picture in a chat — an OpenAI `image_url` part
(or an Ollama `images` entry) reaches the Responses API as an `input_image`. That already worked in v3.68; it is now
tested end to end through the hub's `/v1/chat/completions`.

## What's new

**On the coordinator — `Media` on an `openai-compatible` or `gemini` provider:**

```jsonc
"Providers": {
  "openai": {
    "Type": "openai-compatible",
    "BaseUrl": "https://api.openai.com/v1",
    "Media": {
      "image":      { "gpt-image": "gpt-image-1.5" },
      "image-edit": { "gpt-image": "gpt-image-1.5" },
      "speak":      { "tts": "gpt-4o-mini-tts" },
      "transcribe": { "whisper": "whisper-1" }
    }
  },
  "google": {
    "Type": "gemini",
    "Media": {
      "image":      { "imagen": "imagen-4.0-generate-001", "nano-banana": "gemini-2.5-flash-image" },
      "image-edit": { "nano-banana": "gemini-2.5-flash-image" },
      "speak":      { "gemini-tts": "gemini-2.5-flash-preview-tts" },
      "transcribe": { "gemini-stt": "gemini-2.5-flash" }
    }
  }
}
```

The local name is the consent, exactly as in `ModelMap`, and one name per capability belongs to one provider — a
second claim fails startup naming both. Keys from the environment (`Providers__openai__ApiKey`); a media map without
a key fails startup, and so does `Media` on `anthropic`, `openrouter` or `chatgpt`. Answers carry
`X-InferHub-Served-By: provider:<id>`; `/v1/models` lists a cloud-only name with the kinds it serves; `/api/status`
shows each provider's `media` names.

**When the vendor serves** is the provider's `Policy`, per name: `no-node` (default) — only when no node declares the
model; `prefer` / `only` — the vendor first. **A failed vendor call is not retried on a node**: the vendor may already
have charged.

**On a node — its own `CloudMedia:Providers` section**, `Type` `openai` or `gemini`, its own key
(`CloudMedia__Providers__openai__ApiKey`). The node declares those names as tool capabilities, so a hub routes to it like
any tool, `Node:Capabilities:Disabled` and a hub profile narrow it like one, and solo mode serves it on the same routes.
The hub never sends a node a key.

**OpenAI** is `/images/generations`, `/images/edits`, `/images/variations`, `/audio/speech` and
`/audio/transcriptions` (`verbose_json` with segments for `whisper-1`, so `srt`/`vtt` work there). **Google** is Imagen
through `:predict`, the Gemini image models and TTS models through `:generateContent`, and any Gemini model as a
transcriber (an audio part with a fixed instruction).

## What a vendor cannot do is refused before anything is sent

Each is a 400 naming what works, and no request leaves the host: a negative prompt; panorama seam repair or
reprojection; a mask to Gemini (it edits by instruction); a `luminance` mask to OpenAI (it reads transparent as "edit
here", and inverting a mask means decoding pixels, which no C# here does); a Gemini variation or an Imagen edit; a
Gemini voice in anything but `wav`/`pcm` (it answers raw 24 kHz PCM; an mp3 encoder would be a dependency) or with a
`speed`; a size that is not one of Gemini's aspect ratios (`1024x576` for 16:9); `stream_format` on a cloud voice. Sizes
to OpenAI are passed through and its own 400 relayed. `steps`, `guidance`, `seed` and `strength` are answered without and
listed in the envelope's `warnings`, as is a size sent to Gemini as a ratio — the picture's real size is read off its PNG.

**Units:** a cloud picture meters **one step per megapixel** (zero would make every image quota free the day a key is
set); a voice, characters; a transcript, the seconds the vendor reports (`whisper-1`) or a WAV header's length, and
otherwise **zero** — never a guess from the byte count. **Statuses:** a vendor 4xx is a 400 with its own message, a 429
a 503 with its `Retry-After`, a 401/403 a 502 naming the provider's key (the caller cannot fix it), a 5xx or timeout a
502. No key, prompt, transcript, picture or caller filename is logged; both hosts' cloud `HttpClient`s have no loggers.

**Zero new `PackageReference`** — `HttpClient`, multipart and JSON. `InferHub.Shared.csproj` is still empty.

## Found by running it

Before the tag, a real coordinator and a real meshed node were started from source with their media configured
**through environment variables** (as an operator would), against a stand-in vendor serving OpenAI's and Gemini's
routes:

- **A request refused before the call was counted as the provider failing.** A Gemini voice asked for in mp3 — a 400
  that never left the hub — showed up as `dispatched: 1, failed: 1` on the provider's status. The client now reports
  whether anything was sent, and only a request that reached the vendor counts as a dispatch or a failure.
- **The refusal for a Gemini size suggested `1600x900`**, which the hub's own edge refuses (both sides must be a
  multiple of 8). It suggests `1024x576` now.
- Configuration bound from env with hyphenated names (`Providers__google__Media__image-edit__nano`), the hub routed a
  node's cloud name to the node (`X-InferHub-Served-By: node`) and the node called the vendor with **its own** key; the
  hub's own providers answered with theirs; `srt` from `whisper-1` came back as subtitles; no key, prompt or filename
  in either host's log.

## What was not established, said out loud

- **No call reached OpenAI or Google.** There is no key in this environment. Every request shape — the Images and Audio
  multipart and JSON bodies, `:predict`, `:generateContent` with `responseModalities` and `speechConfig`, the PCM mime
  type — was built from the vendors' documentation and answered by a stand-in. The first real call is the user's.
- **Model ids** in the examples (`gpt-image-1.5`, `imagen-4.0-generate-001`, `gemini-2.5-flash-image`,
  `gemini-2.5-flash-preview-tts`) are the vendors' current names as documented; they move, and nothing here checks them.
- **gpt-4o transcribers report no duration**, so their transcripts meter zero seconds unless the upload is a WAV.
- **Gemini transcription** is a generative model asked to transcribe: no segments (so no `srt`/`vtt`), and its accuracy
  is the model's, not measured here.
- **Uploads over `Tools:MaxAttachmentBytes`** stream to nodes only; a provider-only model refuses them with a 413.

## Upgrading

Nothing changes unless a provider gets a `Media` map or a node gets a `CloudMedia` section. Every route, status payload
and model listing is v3.68's without them.

Tests: `CloudMediaClientTests` (Shared — each dialect and capability through the real renderers), `CloudMediaProviderTests`
(Coordinator — a real Kestrel hub with no node, the real job registry, privacy at Trace, the dispatch counters),
`CloudMediaNodeTests` (Node — a solo node through its composition root), and the ChatGPT picture-input test. Whole
suite: **2186 passed, 63 skipped**.
