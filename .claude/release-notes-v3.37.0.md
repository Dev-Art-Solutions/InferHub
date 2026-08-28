# InferHub v3.37.0 — `/v1/audio/speech` answers while it is still speaking

A paragraph is a couple of minutes of audio. Until this release, `POST /v1/audio/speech` synthesised
all of it, wrote it to a file on the node, shipped the file back across SignalR and handed the caller
a body — and for those two minutes the socket was silent. Every product that reads text aloud starts
playing the first sentence while the rest is still being made, and none of them could be built on
this route.

The plumbing had been there since v3.9 and nothing had ever used it. `ToolExecutor.StreamAsync`'s own
remarks named the missing piece, in writing, for five releases:

> *"A streaming tool response carries no attachments... Chunked binary needs a concatenable format
> and a contract on the client side; that is a phase, not a footnote."*

This is that phase.

## What v3.37 adds

`stream_format`, OpenAI's own field, with both of its values:

```bash
curl -N http://localhost:5080/v1/audio/speech \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"en_US-amy-medium","input":"One. Two. Three.","response_format":"wav","stream_format":"audio"}' \
  --output out.wav

  ... "stream_format":"sse"
#   event: speech.audio.delta   {"type":"speech.audio.delta","audio":"<base64>"}
#   event: speech.audio.done    {"type":"speech.audio.done","usage":{"input_tokens":0,…}}
```

Piper produces one piece of audio per sentence; the worker splits those at 16 KiB, the node forwards
them, and the edge writes them out as they land. **A request that omits `stream_format` is byte for
byte what v3.36.2 returned**, on both hosts.

The decisions worth knowing:

- **Only `wav` and `pcm` stream.** Concatenability is the entire contract. `mp3`, `opus` and `flac`
  need an encoder alive for the length of the request and a chunk boundary that is not a codec frame
  boundary; asking for one is a `400` naming the two that work, never a quiet fall back to buffering.
- **The wav header is 44 bytes written once, from the first chunk, with `0xFFFFFFFF` in both length
  fields.** Piper knows its own sample rate only from its own first samples — the worker has refused
  to hand-set one since v3.10, because a rate that disagrees with the model plays at the wrong pitch
  and passes every byte-count assertion anybody writes. The declared length is the streaming
  sentinel: players accept it, `ffprobe` reports a nonsense duration, and that is said here rather
  than left to be found.
- **The measured rate goes out on `X-InferHub-Audio-Sample-Rate`**, which for `pcm` is the only place
  it can be — and it is why the status is not committed until the first chunk arrives. A refusal is
  still a `400` or a `502` with an error envelope.
- **`usage` in `speech.audio.done` is three zeros and they are true.** The schema requires the object
  so it is written; Piper is a phoneme model and nothing was tokenized. What is billed is unchanged
  from v3.10 — input characters, counted at the edge, also on `X-InferHub-Speech-Characters`.
  Putting a character count into a field named `tokens` is roughly a four-to-one error landing on
  somebody's invoice reconciliation.
- **A mixed fleet keeps working.** Nodes declare `SupportsStreamedSpeech`; a pre-3.37 node serves
  every buffered request it always did and is simply not a candidate for a streaming one. A fleet
  with none new enough answers `503` naming the version, rather than "no node provides speak" —
  which would send an operator to look at their voices.

## The one that is load-bearing, and the bug it is built out of

**A chunk that would not fit the wire is a failed job with a sentence in it, never a dropped
connection.**

v3.10.0 shipped dead on arrival for exactly this reason. SignalR's default
`MaximumReceiveMessageSize` is 32 KB, and exceeding it does not fail the message — it kills the
connection. A six-second wav is ~300 KB, so *every* real `/v1/audio/speech` request through a
coordinator returned a 500, dropped the node's SignalR connection and made it re-register.
`NodeHubLimits` is the scar from that day.

A streaming answer crosses that wire fifty times instead of once, so the same mistake becomes fifty
times likelier to be made by somebody's worker. Two things now stand in the way:

- The worker splits at **16 KiB of PCM** — ~21.8 KB once base64 and the frame envelope are on it,
  under SignalR's own default, so a hub whose operator never raised a limit is safe. (~0.37 s of
  audio at 22.05 kHz, which is also the latency granularity a caller feels.)
- The node refuses to forward a `chunk` frame over `ToolProtocol.MaxChunkPayloadBytes` (30 KiB) and
  ends the job naming both numbers. That constant is deliberately *not* derived from
  `Tools:MaxAttachmentBytes`: the node cannot see the hub's configuration, and a limit that is only
  correct on a generously configured hub is a limit that fails where it matters.

**The worker is retired when that happens**, which is the opposite of what cancel does and was not
the first answer. The first answer left it warm — cancel exists precisely so the next caller is not
made to pay a multi-gigabyte weight load for somebody else's problem. **The test found the hole in
under a minute:** the request after the refused one also came back `502`. Abandoning the stream
without telling the worker leaves its remaining frames queued on the pipe against a request that no
longer exists, and a warm worker hands them to the next caller as their answer. A weight load is the
cheaper mistake.

`AnOversizedChunkFailsTheJobAndTheConnectionSurvivesIt` asserts both halves, and the second half is
the point: it asks the same mesh for something else afterwards. A suite that only checked the failed
request would have passed on v3.10.0's bug.

## Two facts read on the day rather than remembered

- **OpenAI's own Python SDK models neither streaming event.** `src/openai/types/audio/` carries
  `transcription_text_delta_event.py` and has no speech equivalent, so `sse` is a
  documented-but-unmodelled surface and `audio` — `with_streaming_response.create(...).iter_bytes()`
  — is what an SDK actually consumes. Both ship; the docs lead with `audio` because of it.
- **`speech.audio.done`'s `usage` object is required by the published schema**, all three counts.
  That is what forced the zeros to be a decision rather than an omission.

`speech.audio.error` is **ours**. OpenAI's schema defines only `delta` and `done`, and a stream that
dies after the first byte has no status left to send — so `sse` ends with a terminal event carrying
the ordinary error envelope, and `audio` closes, because there is nowhere in a raw byte stream to put
a sentence. Closing silently was considered and rejected: a client holding a 200 and half an answer
cannot tell an ending from a failure.

## Numbers

- `dotnet test InferHub.sln` — **1 542 passed / 48 skipped** at the tag. Two more landed the same
  evening (`NoSynthesisedTextAppearsAnywhereWhenTheAnswerIsStreamed`, one per stream shape) — rule 7
  deserved the streamed path explicitly, since it writes a different log line from a different
  method at a different moment, which is the shape of change that reintroduces that bug. **Tests
  only; the tagged artifact is unchanged**, and 1 544 is the number after them.
- **Zero new `PackageReference`s.** `InferHub.Shared.csproj` is still an empty
  `<Project Sdk="Microsoft.NET.Sdk">`. Rule 5 intact for the twelfth consecutive release.
- **No new configuration key.** There was a `Tools:Speech:MaxChunkBytes` in an early draft and it was
  dropped: two numbers that have to agree are two numbers that will not, which is the argument
  `NodeHubLimits` already makes against its own second key.
- Context files: `src/InferHub.Coordinator/CLAUDE.md` is at **1 084 of 1 100**. The next phase that
  touches the coordinator will have to split it, as 62, 67 and 69 each did. Named here so it is not
  discovered by a failing budget test on somebody's deadline.
- Phase 68 (the provider verification day) has been parked since 2026-08-27 awaiting vendor keys, so
  its reserved version moved to `v3.38.0` and `.claude/verification-v3.37.0.md` was renamed. A
  version number belongs to the release that ships.

## What was not established

Said out loud, in the house habit:

- **No real Piper voice has streamed.** Every test here drives the echo worker, which sends the same
  quarter-second 440 Hz tone it has always written to a file, split into frames. That the *real*
  worker's `synthesize()` yields one chunk per sentence is read out of `piper==1.6.0`'s own
  `voice.py` — `synthesize_wav` is that loop writing into a `wave` file — and not observed. **Nobody
  has listened to a streamed sentence**, and no time-to-first-audio has been measured. That is the
  published-image check on the `:tools` image with a voice file, and it is the first thing to run.
- **No browser has played a `0xFFFFFFFF` wav from this endpoint.** The sentinel is what a piped wav
  has always used and the header is asserted byte by byte, but "a player accepts it" is a claim about
  players.
- **The 16 KiB split has not been felt.** ~0.37 s is arithmetic on the sample rate, not a measured
  gap between frames on a real card.
