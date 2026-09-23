<h2>Piper stays Piper. Bulgarian gets its own engine — and we found three bugs running it before we shipped it.</h2>

<p>InferHub's <code>speak</code> capability has been Piper since phase 42: a small, CPU-comfortable
ONNX voice, chosen because a TTS that needs a card is a TTS most people running this project cannot
use. v3.49 doesn't touch that. It adds a second engine beside it, for a language Piper doesn't cover
well —
<a href="https://huggingface.co/beleata74/bg-tts-v5">beleata74/bg-tts-v5</a> (MIT), a 250.8M-parameter
encoder-decoder Transformer over NVIDIA's NanoCodec, trained on roughly 700 hours of Bulgarian
speech.</p>

<h3>Why a whole new image</h3>

<p>Nothing <code>onnxruntime</code> can load here, and nothing Piper's per-voice
<code>.onnx</code>/<code>.onnx.json</code> pair shape fits. This model needs PyTorch and
<code>nemo_toolkit</code> — not for speech recognition, only for the one audio codec it decodes
through — and a GPU to run at a usable speed. Stacking that onto the existing <code>:diffusion</code>
image would put two independently pinned CUDA builds next to each other in one container: NeMo's own
torch, and <code>diffusers</code>' own torch. "Two engines, whose pin wins" is exactly the question
this project has refused to ask itself since phase 39. So it's a sixth image, <code>:tts-bg</code>,
the same reasoning that gave <code>:diffusion</code> its own container instead of a flag on
<code>:tools</code>.</p>

<pre><code>docker run -d --gpus all -e LocalApi__Enabled=true -e LocalApi__ApiKeys__0=your-key \
  -v inferhub-bg-tts:/data -p 5083:8080 ghcr.io/dev-art-solutions/inferhub-node:tts-bg

curl http://localhost:5083/v1/audio/speech -H "Authorization: Bearer your-key" \
     -H 'Content-Type: application/json' \
     -d '{"model":"bg-tts-v5-spk0","input":"Добър ден! Как сте днес?"}' -o out.wav</code></pre>

<p>The checkpoint is 2.9 GB and isn't published through the usual <code>from_pretrained</code>
convention, so — like Piper's own voices — it's placed by hand rather than auto-fetched on somebody's
first request. The one thing this image does fetch on its own is NVIDIA's NanoCodec, a few hundred
MB with no "serve this voice without it" state to gate.</p>

<h3>Two speakers, two model names — never a field that could pick the wrong one</h3>

<p>The checkpoint bakes in two voices rather than shipping them as separate files: speaker 0 is
AI-generated and fast, speaker 1 is a real voice tuned for longer passages. They're exposed as
<code>bg-tts-v5-spk0</code> and <code>bg-tts-v5-spk1</code> — two models sharing one loaded
checkpoint — because a caller who asked for one speaker and silently got the other has exactly the
problem this project has refused to ship since Piper's own README started warning about it.</p>

<h3>Three bugs in the model's own example code, found by actually running it</h3>

<p>The vendored inference code (MIT, copied in rather than pip-installed, since it isn't published as
a package) had apparently never been run end to end against a current <code>torchaudio</code>. Every
single synthesis failed on the very first attempt — <code>ValueError: Expected 2D Tensor, got
1D.</code> The decoder was already returning the right shape; a defensive <code>.squeeze(0)</code>
one line later was stripping it down to nothing. Fixed, once the audio actually came out: it was
saved as 32-bit float WAV, a format Python's own <code>wave</code> module — which this project uses
to derive raw PCM from a saved WAV — flatly refuses to open. Pinned to 16-bit PCM, which is also
exactly what Piper's own output already is.</p>

<p>Then the worker itself hung forever on its very first request, every single time. This project hit
almost the identical bug three releases ago building its cross-encoder reranker: a native library
import happening on a background thread while the main thread sits in a blocking read is a loader-lock
hazard, not a logic bug. The fix from that release — import once, at boot, on the main thread — turned
out <em>not</em> to be enough by itself here, one dependency layer deeper: the audio codec does its
own lazy import of NVIDIA's NeMo models inside a method that only ever runs on the request thread. It
took forcing that import at boot too before the hang actually went away.</p>

<p>And once it stopped hanging, NeMo's own logger turned out to write straight to the process's real
stdout — the same channel this project's worker protocol uses for one JSON object per line. Redirecting
<code>sys.stdout</code> around the call did nothing, because the logger had already bound its handler
to the original stream object at import time, before the redirect ever ran. Patched once, permanently,
at startup.</p>

<h3>Verified twice — by hand, and against the actual published image</h3>

<p>Both speakers were first driven directly against the model's own code on a real GPU, producing
real, audible Bulgarian audio — this is also where the bugs above surfaced and got fixed. Then, after
the fixes landed, the worker was driven a second time through the real process protocol it actually
speaks in production. And after the tag's GHCR build finished, the published
<code>ghcr.io/dev-art-solutions/inferhub-node:3.49.0-tts-bg</code> image was pulled and run for real:
checkpoint placed by hand, a genuine <code>POST /v1/audio/speech</code> over HTTP came back 200 with
a real WAV file, and <code>/api/status</code> confirmed CUDA was actually visible inside the
container — not just claimed. It was also the first time the loader-lock and logging fixes ran on
Linux at all, and the published image answered its first request cleanly.</p>

<p>What's still open, named rather than implied: the CPU fallback path is written but untested — no
synthesis was attempted without a GPU. The encoded output formats (mp3/opus/flac) share Piper's own
ffmpeg subprocess shape verbatim and weren't separately re-verified here.</p>
