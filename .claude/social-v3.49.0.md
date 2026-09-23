# Social copy — v3.49.0 (phase 84: bg-tts-v5, a second speak engine for Bulgarian)

Blog: https://blog.devart.solutions/blog/inferhub-3-49-piper-stays-piper-bulgarian-gets-its-own-engine
Release: https://github.com/Dev-Art-Solutions/InferHub/releases/tag/v3.49.0

## X / Twitter

InferHub's `speak` capability has been Piper since phase 42. v3.49 adds a second engine beside it —
bg-tts-v5, real Bulgarian TTS — and running the model's own example code for the first time found
three bugs nobody had ever hit, including one identical to a hazard we already fixed three releases
ago, one layer deeper.

https://blog.devart.solutions/blog/inferhub-3-49-piper-stays-piper-bulgarian-gets-its-own-engine

## Facebook / LinkedIn

**Piper stays Piper. Bulgarian gets its own engine.**

InferHub's text-to-speech has been Piper since phase 42 — small, ONNX, comfortable on a CPU, chosen
because a TTS that needs a GPU is a TTS most people can't run. v3.49 doesn't touch that. It adds a
second engine beside it, for a language Piper doesn't cover well: bg-tts-v5, a 250.8M-parameter
Transformer over NVIDIA's NanoCodec, trained on ~700 hours of Bulgarian speech, MIT licensed. It ships
as its own GPU image (`:tts-bg`) rather than a flag — stacking its dependency tree onto the existing
diffusion image would put two independently pinned PyTorch builds in one container, the exact
situation this project has refused to create since phase 39.

Running the model's own example code end to end — not just reading it — found three real bugs before
any of this shipped: every synthesis failed on a tensor-shape bug in the vendored code, the audio that
did come out was saved in a format this project's own PCM extraction can't open, and the worker itself
hung forever on its first request. That last one is nearly the same loader-lock hazard InferHub's
cross-encoder reranker hit three releases ago — a native library import on a background thread while
the main thread sits in a blocking read — except the fix that solved it there wasn't quite enough
here, one dependency layer deeper (the audio codec does its own lazy import inside a method that only
runs on the request thread). All three fixed, then verified twice: once by hand against the raw model
code, and a second time against the actual published container, over a real HTTP request, with CUDA
confirmed genuinely visible inside it.

What's still open, named rather than implied: no CPU-only run was attempted, and the compressed audio
formats (mp3/opus/flac) share Piper's own encoding path but weren't separately re-verified.

https://blog.devart.solutions/blog/inferhub-3-49-piper-stays-piper-bulgarian-gets-its-own-engine
