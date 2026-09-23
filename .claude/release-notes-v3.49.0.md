# InferHub v3.49.0 — bg-tts-v5: a second `speak` engine, for Bulgarian

Piper has served `speak` since phase 42, and it stays exactly as it is — a small, CPU-comfortable
ONNX voice. This release adds a second engine beside it, not a Piper voice:
[`beleata74/bg-tts-v5`](https://huggingface.co/beleata74/bg-tts-v5) (MIT), a 250.8M-parameter
encoder-decoder Transformer over NVIDIA's NanoCodec, trained on ~700 hours of Bulgarian speech.
Nothing `onnxruntime` can load, and nothing Piper's per-voice `.onnx`/`.onnx.json` pair shape fits.

## What's new

A sixth node image, `:tts-bg` — the plain node plus a Python venv, PyTorch and `nemo_toolkit[asr]`
(needed only for the NanoCodec that decodes bg-tts-v5's audio tokens; nothing here touches ASR):

```bash
docker run -d --name inferhub-bg-tts --gpus all \
  -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
  -e LocalApi__ApiKeys__0=your-key \
  -v inferhub-bg-tts:/data -p 5083:8080 \
  ghcr.io/dev-art-solutions/inferhub-node:tts-bg

curl http://localhost:5083/v1/audio/speech -H "Authorization: Bearer your-key" \
     -H 'Content-Type: application/json' \
     -d '{"model":"bg-tts-v5-spk0","input":"Добър ден! Как сте днес?"}' -o out.wav
```

**Two model names, one loaded checkpoint.** The checkpoint bakes in `speaker_id` 0 (AI-generated,
clear, fast) and 1 (a real voice, tuned for 250-320 character passages) rather than shipping as
separate weight files, so `bg-tts-v5-spk0` and `bg-tts-v5-spk1` are declared as two models sharing
one loaded model/tokenizer/codec — the model name is what pins the speaker, never a `voice` field
that could silently drift to the other one.

**No streaming, no `speed`.** The model has no chunked/incremental decode path to hang a `stream`
frame off (unlike Piper's per-sentence synthesis) and no length-scale knob to honour an OpenAI-style
`speed`. Both are refused by name — `invalid_request` / `unsupported_format` — rather than silently
ignored.

**A sixth image, not a flag on `:tools` or `:diffusion`.** `nemo_toolkit`'s dependency tree
(pytorch-lightning, hydra, omegaconf, on top of its own pinned torch) stacked onto `:diffusion` would
put two pinned CUDA builds next to each other — "two engines, whose pin wins" is exactly the question
phase-39 D9 exists to avoid asking. Stacked onto the CPU-sized `:tools`, it would put several GB of
GPU-only PyTorch into every Whisper/Piper deployment.

**The 2.9 GB checkpoint is placed by hand, Piper's shape, not diffusion's.** It is not published
through `huggingface_hub`'s `from_pretrained` convention — one raw `checkpoint.pt` under a
`checkpoint/` folder — and nobody asked the fleet to spend 2.9 GB of a fresh volume's bandwidth on
somebody's first request the way v3.14.0 once did before v3.14.1 fixed it:

```bash
docker exec inferhub-bg-tts mkdir -p /data/tools/bg-tts-v5
curl -L -o /tmp/checkpoint.pt \
  https://huggingface.co/beleata74/bg-tts-v5/resolve/main/checkpoint/checkpoint.pt
docker cp /tmp/checkpoint.pt inferhub-bg-tts:/data/tools/bg-tts-v5/checkpoint.pt
```

NVIDIA's NanoCodec (a few hundred MB) *is* fetched automatically on first use — there is no "offer
this voice without its own codec" state to gate, so `Tools__AllowModelDownload` is `true` in this
image for that one small, unconditional fetch, never for the 2.9 GB checkpoint.

## Three bugs found by actually running the vendored model code, not by reading it

`tts_v5/` (vendored into `bg_tts_v5/`, MIT, not a PyPI dependency) is upstream's own source, and its
own example script had never actually been run end to end against `torchaudio==2.6.0`'s `soundfile`
backend:

1. **Every synthesis failed with `ValueError: Expected 2D Tensor, got 1D.`** `codec.py` did
   `torchaudio.save(output, wav.squeeze(0), ...)`. `decode()` returns `[batch=1, samples]` — already
   the `[channels, samples]` shape torchaudio wants — so squeezing the only leading dim of size 1
   collapsed it to bare 1D. Fixed by only squeezing when the result would still be at least 2D.
2. **`response_format: "pcm"` failed with `wave.Error: unknown format: 3`.** The same call wrote
   IEEE-float WAV by omitting `encoding`. Every InferHub `speak` worker, this one included, derives
   `pcm` from a saved `wav` through Python's own `wave` module, which refuses float WAV. Pinned to
   16-bit PCM, matching what Piper's own `wav` output already is.
3. **A loader-lock hazard, `rerank_worker.py`'s phase-80 D5 one dependency heavier.** The first
   version imported `torch`/`nemo` lazily inside `load()`, Piper's own shape — driven through the
   real process protocol, it hung forever on the very first request, every time, on Windows. Moving
   the import to module scope (rerank's fix) was *not* enough by itself: `CodecV5._load_model` does
   its own lazy `from nemo.collections.tts.models import AudioCodecModel` one level deeper, so that
   import had to be forced at module scope too before `ready` stopped lying about being ready.

A fourth thing, found immediately after: **NeMo's own logger binds a `StreamHandler` directly to the
`sys.stdout` object at import time**, and `contextlib.redirect_stdout` does nothing to it — a handler
that already captured the concrete stream object is unaffected by later reassigning what the *name*
`sys.stdout` points at. The very first `AudioCodecModel.from_pretrained()` call wrote a bare
`[NeMo I ...] Model ... was successfully restored ...` line onto the worker's real stdout — not JSON,
mid-protocol, exactly the corruption `inferhub_worker`'s rule 1 warns about. Patched once,
permanently, at import (`_handlers["stream_stdout"].stream = sys.stderr`), not with NeMo's own
`patch_stdout_handler`, which is a context manager scoped to a single call.

## Verified

**Full regression green:** `dotnet test tests/InferHub.Tests.Node/InferHub.Tests.Node.csproj --filter
FullyQualifiedName~BundledNodeTests` — 31 passed, 0 failed, including six new tests asserting the
`:tts-bg` image does not stack on the other five, enables its opt-ins, fetches only the small codec,
and asserts its own venv imports at build time.

**Synthesis verified for real, on an RTX 3090 Ti, against the actual 2.9 GB checkpoint — twice.**
First hand-run directly against the vendored `tts_v5.inference.synthesize()` (both bugs above
surfaced and were fixed here), producing real, audible Bulgarian speech for both speakers — `spk0`
returned 5.1s of clear audio for a two-sentence prompt; `spk1` (tuned for longer passages) rambled to
21.8s on the same short prompt, matching the model card's own stated limitation rather than a bug in
this integration. Then driven a second time through the actual `bg_tts_worker.py` process, over the
real `hello`/`ready`/`request`/`result` protocol (not a direct function call) — `bg-tts-v5-spk0`
returned a valid `result` frame with a 16-bit PCM file carrying real signal (peak ~24460/32767, not
silence), the loader-lock and stdout-corruption bugs having been found and fixed in this same pass.

**Against the published `:tts-bg` image, after the tag's GHCR build finished — the non-negotiable
check, run for real, the same rule v3.10.0 and five releases since have all followed:**

Pulled `ghcr.io/dev-art-solutions/inferhub-node:3.49.0-tts-bg` (digest
`sha256:d9165072cdd118ae97b19ff002c2e127dd4e6271506aa333d0d043bc719e8ee4`). Ran the container for
real (`--gpus all`, `LocalApi:Enabled=true`), placed the 2.9 GB checkpoint by hand exactly as the
file header documents (`docker cp` into `/data/tools/bg-tts-v5/checkpoint.pt`), and drove it over the
real HTTP API — not the process protocol directly this time, the layer above it:

- `docker logs` on startup: `[bg-tts-v5] offering voices: bg-tts-v5-spk0, bg-tts-v5-spk1 (formats:
  wav, pcm)` and `Tool runtime is on: 1 of 1 manifest(s) started` — the manifest loaded and the
  worker declared both speakers from inside the container, on its own venv, on the first try.
- `GET /api/status` came back `"nodeVersion":"3.49.0"`, `"gpu":{"cuda":true,"devices":1,"names":
  ["NVIDIA GeForce RTX 3090 Ti"]}`, `"capabilities":["speak"]` — CUDA genuinely visible inside the
  container, not just claimed.
- `POST /v1/audio/speech` with `{"model":"bg-tts-v5-spk0","input":"Здравей, свят! Проверка на
  публикувания образ."}` returned `HTTP 200` and a 28,268-byte WAV: 22,050 Hz, 16-bit, 0.64s, peak
  ~19690/32767 — real signal, not silence, not an error page.
- The same endpoint with `"response_format":"pcm"` also returned `HTTP 200` with a headerless PCM
  body — the format-selection path works end to end through the real HTTP layer, not only inside the
  worker's own protocol frames.
- **This is also the first time the loader-lock and NeMo stdout-logging fixes (D4/D5 above) ran on
  Linux at all.** The published image answered its very first request without hanging and without a
  corrupted frame — consistent with the fixes being harmless there, though the counterfactual
  (the pre-fix code, on Linux) was never tried, so this is evidence rather than a controlled
  comparison.

## What is still not established

- **The CPU fallback path is read, not run.** `device()` falls back to CPU with a logged warning the
  same way `rerank_worker.py` does; no synthesis was attempted without a GPU, on Windows or in the
  published container.
- **The encoded formats (`mp3`/`opus`/`flac`) share `piper_worker.py`'s `ffmpeg` subprocess shape
  verbatim and were not separately re-verified here**, on the hand-run venv or the published image.
- **`bg-tts-v5-spk1` was not re-verified against the published image** — only `spk0`, over HTTP. The
  hand-run verification above already found it can run to several times the length of a short prompt
  (a model characteristic, not a bug), which the published-image pass did not repeat.

## Site and blog copy

- `inferhub.devart.solutions` changelog row: "v3.49.0 — bg-tts-v5, a second `speak` engine for
  Bulgarian TTS (new `:tts-bg` image, GPU required, checkpoint placed by hand)."
- Blog: `blog-v3.49.0.md`, posted to blog.devart.solutions.
- Social: `social-v3.49.0.md`.
