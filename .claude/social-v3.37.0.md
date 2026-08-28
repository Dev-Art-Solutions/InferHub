# Social copy — v3.37.0

Unposted. Iliya posts by hand (no connector).

**Post link:** https://blog.devart.solutions/blog/inferhub-3-37-speech-that-starts-before-it-is-finished
(slug `inferhub-3-37-speech-that-starts-before-it-is-finished`, ID `6a90d7afbaed2882b3172522`,
EN-visible / BG-hidden.)

## X — the product angle (250 chars; the link counts as 23)

> A paragraph is two minutes of audio. InferHub used to be silent for all of it, then hand you a
> file.
>
> v3.37: OpenAI's stream_format on /v1/audio/speech.
>
> First byte at 0.205s instead of 1.228s. Same total. You just stop waiting for it.
>
> [link]

## X — the bug angle (252 chars), stronger for people who ship their own software

> SignalR's default message limit is 32 KB. Go over it and it does not fail the message — it kills
> the connection.
>
> That bug cost us a release in 2026. Adding streaming audio meant crossing that wire 50× per
> request instead of once.
>
> Here is what we did about it: [link]

## X — the honest-notes angle (254 chars), the most distinctive of the three

> Shipped streaming TTS, then pulled the image before writing the post.
>
> It deleted a sentence we'd put in three documents: ffprobe does NOT choke on a streaming wav
> header. It reports the right duration.
>
> Fixed everywhere. A published post can't be.
>
> [link]

*Counts include the URL as 23. No backticks: X renders them literally.*

## Facebook / LinkedIn

> **InferHub v3.37 — speech that starts before it is finished.**
>
> A paragraph is a couple of minutes of audio. Until this release, `/v1/audio/speech` synthesised
> all of it before the caller heard anything — the whole input to a worker, a file written, the file
> across the wire, one body handed over. Two minutes of silent socket.
>
> Every product that reads text aloud starts playing the first sentence while the rest is still
> being made. None of them could be built on that route.
>
> v3.37 adds OpenAI's own `stream_format`, both of its values: raw bytes on a chunked body, or
> `speech.audio.delta` / `speech.audio.done` events. Piper produces one piece of audio per sentence
> and they go out as they are made. **Leave the field out and the response is byte for byte what
> 3.36 returned** — on a coordinator and on a standalone node alike.
>
> Three calls that are not obvious:
>
> • **Only `wav` and `pcm` stream.** Concatenability is the entire contract. mp3, opus and flac need
> an encoder alive for the length of the request, and a chunk boundary is not a codec frame
> boundary. Asking is a 400 naming the two that work — never a quiet fall back to buffering, because
> a caller who asked to stream and silently got a two-minute wait has a bug with nothing in the
> response to explain it.
>
> • **The wav header is 44 bytes written once, with 0xFFFFFFFF in both length fields.** Piper knows
> its own sample rate only from its own first samples, so the header is built from what the audio
> measured. The declared length is the streaming sentinel — players accept it, ffprobe reports a
> nonsense duration, and that is in the docs rather than left to be found.
>
> • **The usage object in `speech.audio.done` is three zeros, and they are true.** The schema
> requires all three token counts, so all three are written. Piper is a phoneme model; nothing was
> tokenized. What is actually billed is unchanged — input characters, on a header. A character count
> in a field named "tokens" is a four-to-one error on somebody's invoice.
>
> And then the half that is really about a bug from 3.10.
>
> SignalR's default receive limit is 32 KB, and exceeding it **does not fail the message — it kills
> the connection**. A six-second wav is ~300 KB, so v3.10.0 shipped dead on arrival: every real
> speech request dropped the node and made it re-register. The tests had verified attachments across
> that wire with a 16-byte file, four orders of magnitude under the limit. They proved the plumbing
> and said nothing about the ceiling.
>
> A streaming answer crosses that wire fifty times instead of once — and it is now a mistake
> somebody else's worker can make, because "one chunk per sentence" is not a size. So the worker
> splits at 16 KiB, under SignalR's own default, and the node refuses to forward anything larger,
> ending the job with both numbers in the sentence rather than dropping a connection every other
> client on that box is using.
>
> The first version of that left the worker warm — obviously right, since cancellation here is
> cooperative precisely so the next caller doesn't pay a weight reload for somebody else's problem.
> The test asked the same mesh for something else immediately afterwards, and that failed too:
> abandoning a stream without telling the worker leaves its frames on the pipe for a request that no
> longer exists, and a warm worker hands them to the next caller as their answer. Found in under a
> minute, because the test asks a second question.
>
> Then we pulled the published image and ran a real voice through it. One 311-character paragraph:
> **first byte at 0.205s streamed against 1.228s buffered**, same total — six times sooner to
> something you can hear. Five SSE frames of exactly 16 384 bytes, and a done event with three
> zeros in it.
>
> It also deleted one of our own sentences. Three of this release's documents claimed ffprobe would
> report a nonsense duration on a streaming wav header. It does not — a saved file has a byte count
> and ffmpeg prefers it. Corrected in all four places, and it could only be corrected because the
> image was pulled *before* the post went out.
>
> One more thing worth knowing if you ever diff audio between releases: three syntheses of one
> identical sentence came back 289 836 / 286 252 / 284 204 bytes. Piper's sampling is not
> deterministic.
>
> **Still not established, said plainly:** nobody has actually listened to it, and every number
> above is a standalone node rather than a fleet.
>
> Zero new dependencies, twelfth release running. No new configuration key.
>
> Full write-up: [link]

## Notes

- **No image.** The honest visual for a streaming release is a waveform arriving. A spectrogram of
  the real amy voice would now be fair — the audio exists — but nothing has been *listened* to, and
  a picture of a waveform is a claim about sound.
- The three X variants are alternatives, not a thread. The honest-notes one is the most distinctive
  and the least flattering; it is here because it is the one this project would actually stand
  behind.
- If the FB copy needs shortening, cut everything from "And then the half that is really about a bug
  from 3.10" to "because the test asks a second question" — the product half stands alone.
