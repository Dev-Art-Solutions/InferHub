# Blog post — v3.37.0

Slug: `inferhub-3-37-speech-that-starts-before-it-is-finished`
Title (EN): **InferHub 3.37 — speech that starts before it is finished**
EN visible / BG hidden. Author: Admin.

Excerpt (EN): A paragraph is two minutes of audio and InferHub was silent for all of it. 3.37 adds
OpenAI's stream_format — first byte at 0.205s instead of 1.228s — and the image check killed one of
our own sentences on the way out.

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

<p>On the published image, with a real voice, one 311-character paragraph:</p>

<table>
<tr><th></th><th>Time to first byte</th><th>Total</th></tr>
<tr><td>Buffered (3.36 behaviour)</td><td><strong>1.228 s</strong></td><td>1.237 s</td></tr>
<tr><td><code>stream_format: "audio"</code></td><td><strong>0.205 s</strong></td><td>1.263 s</td></tr>
</table>

<p><strong>Six times sooner to the first sample, and the same total.</strong> Nothing got faster. The first byte just leaves earlier, which is the only part a listener can hear.</p>

<h2>Three decisions that are not obvious</h2>

<p><strong>Only <code>wav</code> and <code>pcm</code> stream.</strong> Concatenability is the entire contract. <code>mp3</code>, <code>opus</code> and <code>flac</code> need an encoder process alive for the length of the request, and a chunk boundary is not a codec frame boundary — a naive split clicks every third of a second. Asking for one is a <code>400</code> naming the two that work, never a quiet fall back to buffering. A caller who asked to stream and silently got a two-minute wait has a bug with nothing in the response to explain it.</p>

<p><strong>The wav header is 44 bytes written once, with <code>0xFFFFFFFF</code> in both length fields.</strong> Piper knows its own sample rate only from its own first samples — the worker has refused to hand-set one since 3.10, because a rate that disagrees with the model plays at the wrong pitch and passes every byte-count assertion anybody writes. So the header is built from the audio's own measurement, and the declared length is the streaming sentinel. The measured rate also goes out on <code>X-InferHub-Audio-Sample-Rate</code>, which for <code>pcm</code> is the only place it can be.</p>

<p><strong>The <code>usage</code> object in <code>speech.audio.done</code> is three zeros, and they are true.</strong> OpenAI's schema requires all three token counts, so all three are written — an SDK that models the schema would throw on their absence. But Piper is a phoneme model and nothing in the request was ever tokenized, so zero is the count rather than a placeholder. What is actually billed is unchanged since 3.10: input characters, counted at the edge, and also on a response header. Putting a character count into a field named <code>tokens</code> would be roughly a four-to-one error landing on somebody's invoice reconciliation.</p>

<p>Two facts here were read out of OpenAI's published schema on the day rather than remembered. One is worth passing on: <strong>their own Python SDK models neither streaming event.</strong> There is a type for the transcription delta and none for the speech one. So <code>sse</code> is a documented-but-unmodelled surface, and <code>audio</code> — the streaming-response byte iterator every client already knows — is what an SDK actually consumes. Both ship; the docs lead with <code>audio</code> because of it.</p>

<h2>The half that is really about a bug from 3.10</h2>

<p>v3.10.0 shipped dead on arrival, and this release is built out of the reason.</p>

<p>SignalR's default receive limit is 32 KB, and exceeding it <strong>does not fail the message — it kills the connection</strong>. A six-second synthesised wav is about 300 KB. So every real speech request through a coordinator returned a 500, dropped the node's connection, and made it re-register. The unit tests had verified attachments across the wire with a 16-byte file: four orders of magnitude under the limit. They proved the plumbing and said nothing about the ceiling.</p>

<p>A streaming answer crosses that wire fifty times instead of once. The same mistake becomes fifty times likelier — and it is now a mistake somebody <em>else's</em> worker can make, because a worker is a child process anybody can write, and "one chunk per sentence" is not a size. A caller may send four hundred words with no full stop in them.</p>

<p>So the size is settled twice. The worker splits at 16 KiB of PCM — about 21.8 KB once base64 and the frame envelope are on it, under SignalR's own default, so a hub whose operator never raised a limit is safe. And the node <strong>refuses to forward anything larger</strong>, ending the job with both numbers in the sentence rather than dropping a connection every other client on that box is using.</p>

<h2>The thing the test found in under a minute</h2>

<p>The first version of that refusal left the worker warm. That looked obviously right: this project's cancellation is cooperative precisely so the next caller is not made to pay a multi-gigabyte weight reload for somebody else's problem.</p>

<p>The test asked the same mesh for something else immediately afterwards. It also failed.</p>

<p>Abandoning a stream without telling the worker leaves its remaining frames queued on the pipe, addressed to a request that no longer exists — and a warm worker hands them to the next caller, as their answer. A weight load is the cheaper mistake. The worker is retired.</p>

<p>That second assertion is the whole shape of the test, and it is deliberate: a suite that only checked the failed request would have passed on 3.10.0's original bug too. The difference between a failed job and a killed connection is only visible if you ask the fleet for something else afterwards.</p>

<h2>Then we pulled the image, and it deleted one of our own sentences</h2>

<p>The release notes, the README and the docs site all said the same confident thing about that <code>0xFFFFFFFF</code> length field: a player would accept it, and <code>ffprobe</code> would report a nonsense duration.</p>

<p><strong>It does not.</strong> <code>ffprobe</code> on a streamed file reports <code>duration=23.904943</code> — correct to the sample — because a saved file <em>has</em> a byte count and ffmpeg prefers it over the header. The true statement is narrower, and it is now the one in all four places: a consumer that trusts that field alone computes about four gigabytes, and every consumer that matters falls back to the bytes it actually has.</p>

<p>Small correction, and it only exists because the image was pulled before the post was written. A blog post cannot be amended after it goes out; a wrong sentence in one is permanent.</p>

<p>The same run turned up something worth knowing if you ever diff audio between releases. Three buffered syntheses of one <em>identical</em> sentence came back at <strong>289 836</strong>, <strong>286 252</strong> and <strong>284 204</strong> bytes. Piper's sampling is not deterministic — the same text is a different length every time. That is why the buffered and streamed rows in the table above differ by five kilobytes, and it is not the transport.</p>

<h2>Upgrading</h2>

<p>Nothing to do. No new configuration key — there was one in an early draft (<code>Tools:Speech:MaxChunkBytes</code>) and it was dropped, because two numbers that have to agree are two numbers that will not. Zero new dependencies, for the twelfth consecutive release. A pre-3.37 node keeps serving every buffered request it always did and is simply not a candidate for a streaming one; a fleet with none new enough answers <code>503</code> naming the version, rather than claiming it cannot synthesise at all.</p>

<h2>Still not established</h2>

<p>Said plainly, as always. <strong>Nobody has actually listened to it.</strong> The bytes are right, the sample rate is right, the durations are right and ffmpeg opens the file — but no human ear has been applied and no browser has played one from this endpoint. And every number above is a standalone node, so the version narrowing and its <code>503</code> are covered by the test suite rather than by the artefact.</p>

<p>Release notes, with the full image-check table: <a href="https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.37.0">v3.37.0 on GitHub</a>. Docs: <a href="https://inferhub.devart.solutions/#idocs_speech_streaming">Streaming speech</a>.</p>
