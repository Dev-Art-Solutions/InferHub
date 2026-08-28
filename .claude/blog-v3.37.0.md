# Blog post — v3.37.0

Slug: `inferhub-3-37-speech-that-starts-before-it-is-finished`
Title (EN): **InferHub 3.37 — speech that starts before it is finished**
EN visible / BG hidden. Author: Admin.

Excerpt (EN): A paragraph is two minutes of audio, and InferHub was silent for all of it. 3.37 adds
OpenAI's `stream_format` — and the interesting half is a 32 KB limit that kills connections rather
than messages.

> **No shell commands in the HTML** — the blog sits behind a Cloudflare WAF that blocks the request,
> not the command. JSON bodies only.

---

## content_en

<p>A paragraph is a couple of minutes of audio. Until this release, <code>POST /v1/audio/speech</code> synthesised all of it before the caller heard anything: the whole input went to a worker, the worker wrote a file, the file crossed the wire, and the client got one body. For those two minutes the socket was silent.</p>

<p>Every product that reads text aloud — a chat UI, a reader, an IVR — starts playing the first sentence while the rest is still being made. None of them could be built on that route.</p>

<h2>The comment that had been sitting there for five releases</h2>

<p>The plumbing for a streaming answer has existed since v3.9. A node can send partial results, the hub can forward them, the edge can write them out. Nothing had ever used it for audio, and the method that would have to carry it said why, in writing:</p>

<blockquote><p><em>"A streaming tool response carries no attachments... Chunked binary needs a concatenable format and a contract on the client side; that is a phase, not a footnote."</em></p></blockquote>

<p>This release is that phase.</p>

<h2>What 3.37 adds</h2>

<p>OpenAI's own <code>stream_format</code>, with both of its values. Raw bytes on a chunked body:</p>

<pre><code>{
  "model": "en_US-amy-medium",
  "input": "One. Two. Three.",
  "response_format": "wav",
  "stream_format": "audio"
}</code></pre>

<p>…or the same synthesis framed as events, <code>speech.audio.delta</code> carrying base64 and a terminal <code>speech.audio.done</code>. Piper produces one piece of audio per sentence; the worker splits those, the node forwards them, and the edge writes them as they land.</p>

<p><strong>A request that leaves the field out is byte for byte what 3.36 returned</strong> — on a coordinator and on a standalone node alike.</p>

<h2>Three decisions that are not obvious</h2>

<p><strong>Only <code>wav</code> and <code>pcm</code> stream.</strong> Concatenability is the entire contract. <code>mp3</code>, <code>opus</code> and <code>flac</code> need an encoder process alive for the length of the request, and a chunk boundary is not a codec frame boundary — a naive split clicks every third of a second. Asking for one is a <code>400</code> naming the two that work, never a quiet fall back to buffering. A caller who asked to stream and silently got a two-minute wait has a bug with nothing in the response to explain it.</p>

<p><strong>The wav header is 44 bytes written once, with <code>0xFFFFFFFF</code> in both length fields.</strong> Piper knows its own sample rate only from its own first samples — the worker has refused to hand-set one since 3.10, because a rate that disagrees with the model plays at the wrong pitch and passes every byte-count assertion anybody writes. So the header is built from the audio's own measurement, and the declared length is the streaming sentinel. Players accept it; <code>ffprobe</code> reports a nonsense duration. That is written in the docs rather than left to be discovered. The measured rate also goes out on <code>X-InferHub-Audio-Sample-Rate</code>, which for <code>pcm</code> is the only place it can be.</p>

<p><strong>The <code>usage</code> object in <code>speech.audio.done</code> is three zeros, and they are true.</strong> OpenAI's schema requires all three token counts, so all three are written — an SDK that models the schema would throw on their absence. But Piper is a phoneme model and nothing in the request was ever tokenized, so zero is the count rather than a placeholder. What is actually billed is unchanged since 3.10: input characters, counted at the edge, and also on a response header. Putting a character count into a field named <code>tokens</code> would be roughly a four-to-one error landing on somebody's invoice reconciliation.</p>

<p>Two facts here were read out of OpenAI's published schema on the day rather than remembered. One of them is worth passing on: <strong>their own Python SDK models neither streaming event.</strong> There is a type for the transcription delta and none for the speech one. So <code>sse</code> is a documented-but-unmodelled surface, and <code>audio</code> — the streaming-response byte iterator every client already knows — is what an SDK actually consumes. Both ship; the docs lead with <code>audio</code> because of it.</p>

<h2>The half that is really about a bug from 3.10</h2>

<p>v3.10.0 shipped dead on arrival, and this release is built out of the reason.</p>

<p>SignalR's default receive limit is 32 KB, and exceeding it <strong>does not fail the message — it kills the connection</strong>. A six-second synthesised wav is about 300 KB. So every real speech request through a coordinator returned a 500, dropped the node's connection, and made it re-register. The unit tests had verified attachments across the wire with a 16-byte file: four orders of magnitude under the limit. They proved the plumbing and said nothing about the ceiling.</p>

<p>A streaming answer crosses that wire fifty times instead of once. The same mistake becomes fifty times likelier — and it is now a mistake somebody <em>else's</em> worker can make, because a worker is a child process anybody can write, and "one chunk per sentence" is not a size. A caller may send four hundred words with no full stop in them.</p>

<p>So the size is settled twice. The worker splits at 16 KiB of PCM — about 21.8 KB once base64 and the frame envelope are on it, under SignalR's own default, so a hub whose operator never raised a limit is safe. And the node <strong>refuses to forward anything larger</strong>, ending the job with both numbers in the sentence rather than dropping a connection every other client on that box is using.</p>

<h2>And the thing the test found in under a minute</h2>

<p>The first version of that refusal left the worker warm. That looked obviously right: this project's cancellation is cooperative precisely so the next caller is not made to pay a multi-gigabyte weight reload for somebody else's problem.</p>

<p>The test asked the same mesh for something else immediately afterwards. It also failed.</p>

<p>Abandoning a stream without telling the worker leaves its remaining frames queued on the pipe, addressed to a request that no longer exists — and a warm worker hands them to the next caller, as their answer. A weight load is the cheaper mistake. The worker is retired.</p>

<p>That second assertion is the whole shape of the test, and it is deliberate: a suite that only checked the failed request would have passed on 3.10.0's original bug too. The difference between a failed job and a killed connection is only visible if you ask the fleet for something else afterwards.</p>

<h2>Upgrading</h2>

<p>Nothing to do. No new configuration key — there was one in an early draft (<code>Tools:Speech:MaxChunkBytes</code>) and it was dropped, because two numbers that have to agree are two numbers that will not. Zero new dependencies, for the twelfth consecutive release. A pre-3.37 node keeps serving every buffered request it always did and is simply not a candidate for a streaming one; a fleet with none new enough answers <code>503</code> naming the version, rather than claiming it cannot synthesise at all.</p>

<h2>What has not been established</h2>

<p>Said plainly, as always. <strong>No real Piper voice has streamed, and nobody has listened to one.</strong> Every test drives the echo worker, which sends the same quarter-second tone it has always written to a file, split into frames. That the real worker yields one chunk per sentence is read out of the pinned library's own source — its file writer is that same loop — and not observed. Time to first audio has not been measured, and no browser has played a <code>0xFFFFFFFF</code> wav from this endpoint. That is the first thing to run on the published <code>:tools</code> image with a voice file on the volume.</p>

<p>Release notes and the full argument: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.37.0">v3.37.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_speech_streaming">Streaming speech</a>.</p>
