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

## What is still not established

- **Linux behaviour for the loader-lock and stdout-logging fixes.** Both were found and fixed on
  Windows; this session had no Linux box to cross-check against, the same caveat phase-80 D5 carried.
  The fixes cost nothing on Linux and everything on Windows, so they stay regardless.
- **The CPU fallback path is read, not run.** `device()` falls back to CPU with a logged warning the
  same way `rerank_worker.py` does; no synthesis was attempted without a GPU.
- **The encoded formats (`mp3`/`opus`/`flac`) share `piper_worker.py`'s `ffmpeg` subprocess shape
  verbatim and were not separately re-verified here.**
- **No image has been built or pulled from GHCR yet for this tag** — the checks above ran against a
  hand-built local venv on the box doing the work, not the published `:tts-bg` container. See the
  next release notes update for that, InferHub's own standing rule (v3.10.0, and five times since).

## Site and blog copy

- `inferhub.devart.solutions` changelog row: "v3.49.0 — bg-tts-v5, a second `speak` engine for
  Bulgarian TTS (new `:tts-bg` image, GPU required, checkpoint placed by hand)."
- Blog angle: "Piper stays Piper — Bulgarian gets its own engine, and three bugs in the vendored
  model code we found (and fixed) running it for real before shipping it."
