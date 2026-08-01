# Social — v3.10.0 / v3.10.1

Post manually. Two things to lead with, and the feature is only one of them: **the CPU number**
(because everyone quotes GPU throughput and the other row is what decides whether you can use this),
and **the three bugs that shipped anyway**. This blog has posted "the tests were green and it was
broken" before and those posts do better than the feature announcements, because everybody has one.

Link to the **release**, and say **3.10.1** — 3.10.0 is dead on arrival.

## Facebook

InferHub 3.10: speech in, speech out, on your own box.

Two endpoints, on OpenAI's audio API exactly — /v1/audio/transcriptions and /v1/audio/speech. Which means an app that already transcribes moves over by changing a base URL. Whisper and Piper, in one container, on hardware you own. No account, no per-minute pricing, no recording of anybody's voice leaving the building.

Here is the number everybody skips. RTX 3090 Ti, whisper-small, 113 seconds of audio:

• GPU, float16 — 3.4 s (~33× real time)
• CPU, int8, no GPU passed in — 13.5 s (~8× real time)

The transcripts are identical except for the capitalisation of two proper nouns. Eight times faster than real time on a CPU core is the interesting row: an hour of recording in about seven minutes, on a box with no card in it. Everyone quotes the first number. The second one is what decides whether you can actually use this.

Why is the Python a child process and not a library? Because one bad import takes the node down. A segfault in a native extension loaded into your process is not an exception you catch — it is a process that vanishes mid-stream with every in-flight job. We wrote that down in 3.9 and 3.10 is the release that had to live with it. Zero new dependencies, eleventh release running.

Nothing is silently substituted. A worker always returns text + segments + duration and the EDGE formats json/text/srt/vtt/verbose_json — so no worker author ever writes a subtitle timestamp, and two workers can't disagree about where the comma goes. (SubRip uses a comma before the milliseconds and WebVTT uses a period. That's the single most common way a hand-rolled subtitle file plays in one player and not another.) A format that can't be produced is a 400 naming the ones that can — never a wav labelled audio/mpeg, which is a corrupted file with a confident content type that you find out about in a media player three days later.

And none of the audio is kept. No temp file, nothing containing audio bytes or transcript text in any log at any level — not even the filename you uploaded, because board-meeting.m4a is metadata about somebody's day and isn't needed to run a fleet. The usage ledger gains a duration, never a transcript. There's a test that runs a real transcription through a real mesh with a capturing logger and fails if a known phrase turns up anywhere in the logs or the ledger. That's the difference between a policy and a property.

Then it shipped broken. Three ways. Use 3.10.1.

Every test was green. All seven bugs were found by pulling the published image and running it on a real GPU box — which is a step in our release checklist for exactly this reason, and the fifth time it's been the only thing that caught it.

The one worth your time: SignalR's default maximum message size is 32 KB, and exceeding it does not fail the message — it tears down the connection. A six-second synthesised wav is about 300 KB. So every real speech response through a coordinator was a 500 that also dropped the node. We had shipped attachments in 3.9 and verified them across a real wire — with a 16-byte file. Four orders of magnitude under the cap. The test proved the plumbing and said nothing at all about the size.

The wire limit is now derived from the attachment limit, because two numbers that have to agree are two numbers that won't.

👉 https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.10.1

## X / Twitter — single post (262/280; the link counts as 23 under t.co)

InferHub 3.10: speech in, speech out, on your own box. OpenAI's audio API exactly — a base-URL change.

113s of audio, whisper-small:
GPU 3.4s
CPU, no card, 13.5s

Everyone quotes the first number. The second decides if you can use it.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.10.1

## X / Twitter — the bug post (single, 268/280)

Shipped a release where every test was green and the feature was dead on arrival.

SignalR's default max message size is 32KB. Exceeding it doesn't fail the message — it kills the connection.

We'd tested attachments over a real wire. With a 16-byte file.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.10.1

## X / Twitter — thread (each under 280; link only on 5/5)

**1/5** (241)

InferHub 3.10: a node can transcribe and speak.

/v1/audio/transcriptions and /v1/audio/speech, on OpenAI's API exactly — so an app that already does this moves over by changing a base URL.

Whisper + Piper, one container, hardware you own.

**2/5** (253)

The number everyone skips. RTX 3090 Ti, whisper-small, 113s of audio:

GPU float16 — 3.4s
CPU int8, no card — 13.5s

Same transcript bar two capital letters. ~8× real time on a CPU core: an hour of recording in seven minutes, on a box with nothing in it.

**3/5** (259)

A worker always returns text + segments + duration and the *edge* formats srt/vtt/json.

So no worker author ever writes a subtitle timestamp, and two workers can't disagree about the comma. (SubRip uses one before the ms. WebVTT uses a period. Ask me how I know.)

**4/5** (274)

Then it shipped broken. Three ways. Use 3.10.1.

SignalR's default max message size is 32KB and exceeding it kills the connection, not the message. A 6-second wav is 300KB.

We had tested attachments over a real wire in 3.9. With a 16-byte file.

**5/5** (255 incl. link)

Every test green. All seven found by pulling the image and running it on a real GPU box.

That's a step in our checklist, and the fifth time it's been the only thing that caught it.

Zero new dependencies, still.
https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.10.1

## LinkedIn (if used)

Same as Facebook, minus the last two paragraphs. The audience there is less interested in the bug
and more in "8× real time on a CPU" — lead with the table.
