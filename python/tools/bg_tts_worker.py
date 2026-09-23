#!/usr/bin/env python3
"""Bulgarian text to speech for InferHub, on bg-tts-v5.

    python -u bg_tts_worker.py

A second ``speak`` engine beside Piper, not a Piper voice. ``beleata74/bg-tts-v5``
(https://huggingface.co/beleata74/bg-tts-v5, MIT) is a 250.8M-parameter encoder-decoder
Transformer over NVIDIA's NanoCodec, trained on ~700 hours of Bulgarian speech — nothing
onnxruntime can load, and nothing Piper's per-voice ``.onnx``/``.onnx.json`` pair shape fits. It
needs ``torch`` + ``nemo_toolkit`` (for the codec) and a GPU to run at a usable speed, which is why
it ships as its own image (``:tts-bg``) rather than joining the CPU-sized ``:tools`` one — the same
split ``requirements-diffusion.txt`` already draws for the same reason.

TWO SPEAKERS, TWO MODEL NAMES. The checkpoint bakes in ``speaker_id`` 0 (AI-generated, clear,
fast) and 1 (a real voice, tuned for 250-320 character passages) rather than shipping as separate
weight files, so ``bg-tts-v5-spk0`` and ``bg-tts-v5-spk1`` are declared as two models over one
loaded checkpoint — a caller who asked for one speaker and got the other has the same wrong-voice
problem Piper's own README warns about, and the model name is what pins it rather than a `voice`
field that could silently drift.

``tts_v5/`` (this directory's ``bg_tts_v5`` package) is vendored source, not a PyPI dependency —
see ``bg_tts_v5/NOTICE.md``. It prints to stdout on import and on every load, which would corrupt
the one-JSON-object-per-line protocol (inferhub_worker rule 1), so every call into it here runs
under ``contextlib.redirect_stdout(sys.stderr)``.

NO STREAMING, NO ``speed``. The model has no chunked/incremental decode path to hang a `stream`
frame off (unlike Piper's per-sentence ``synthesize()``) and no length-scale equivalent to honour
an OpenAI-style ``speed`` — so both are refused by name rather than silently ignored, the same
"never a silent substitution" rule ``piper_worker.py`` follows for an unknown voice.

``torch`` (and, through ``bg_tts_v5.codec``, ``nemo``) ARE IMPORTED HERE AT MODULE SCOPE, ON THE
MAIN THREAD — not lazily inside ``load()`` the way Piper's own voice loading is. FOUND BY RUNNING
THIS WORKER THROUGH THE REAL PROCESS PROTOCOL, not by reading it: ``Worker`` dispatches a request
onto its own thread (v3.16.2's shape), and the first version — lazy imports inside ``load()`` —
hung forever on its first request, every time, on Windows. This is ``rerank_worker.py``'s phase-80
D5 hazard again, one dependency heavier: a main thread parked in a blocking ``stdin`` read plus a
second thread pulling in ``torch`` and ``nemo``'s native extensions for the first time deadlocks
inside CPython's import machinery. Paying that cost once at boot means ``ready`` genuinely means
ready. Not confirmed as Linux-specific-safe either way here (this worker has only been driven on
Windows so far — see the release notes) but the fix costs nothing on Linux and everything on
Windows, so it stays.
"""

from __future__ import annotations

import contextlib
import os
import shutil
import subprocess
import sys
import wave

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from inferhub_worker import (  # noqa: E402
    ERROR_INVALID_REQUEST,
    ERROR_MODEL_UNAVAILABLE,
    ERROR_UNSUPPORTED_FORMAT,
    Request,
    ToolError,
    Worker,
)

import torch  # noqa: E402

# `bg_tts_v5.codec.CodecV5._load_model` does this same import, but LAZILY, inside a method that
# only ever runs from `load()` on the request-handling thread — so importing the `bg_tts_v5.codec`
# *module* up here (below) was not enough by itself to dodge the loader-lock hazard the block
# comment above describes: `nemo.collections.tts.models`'s own native extensions still loaded on
# the request thread, and the hang below was found with this line absent. Forcing it here, ahead of
# `CodecV5`'s own import, is what actually moves the cost to boot.
import nemo.collections.tts.models  # noqa: E402,F401

# NeMo's own logger binds a `StreamHandler` directly to the `sys.stdout` object at import time —
# found by running this worker for real: `contextlib.redirect_stdout(sys.stderr)` around `load()`
# does nothing for it, because a handler already holding a reference to the concrete stream object
# is unaffected by later reassigning the *name* `sys.stdout` points at. Without this, the very first
# `AudioCodecModel.from_pretrained()` call wrote a bare `[NeMo I ...] Model ... was successfully
# restored ...` line onto the worker's real stdout — a line that is not JSON, landing mid-protocol,
# which is exactly the corruption inferhub_worker's rule 1 warns about. Patched permanently, once,
# here — not with `patch_stdout_handler`'s context-manager form, which only holds for its own scope.
from nemo.utils import logging as _nemo_logging  # noqa: E402

with contextlib.suppress(KeyError):
    _nemo_logging._handlers["stream_stdout"].stream = sys.stderr

from bg_tts_v5.codec import CodecV5  # noqa: E402
from bg_tts_v5.config import CODEC_NUM_CODEBOOKS  # noqa: E402
from bg_tts_v5.inference import generate  # noqa: E402
from bg_tts_v5.model import load_for_inference  # noqa: E402
from bg_tts_v5.tokenizer import TTSTokenizer  # noqa: E402

TOOL_ID = "bg-tts-v5"

#: model name -> speaker_id baked into the one checkpoint.
SPEAKERS = {"bg-tts-v5-spk0": 0, "bg-tts-v5-spk1": 1}

NATIVE_FORMATS = ("wav", "pcm")
ENCODED_FORMATS = ("mp3", "opus", "flac")

# Library defaults (tts_v5/inference.py's own `synthesize()` signature), not the README's example
# call — the README's 0.25/50/0.8 is one author's tuned example for one piece of text, and copying
# it here would make it this worker's silent opinion about every caller's text.
DEFAULT_TEMPERATURE = 0.7
DEFAULT_TOP_K = 250
DEFAULT_TOP_P = 0.95
DEFAULT_REP_PENALTY = 1.1
DEFAULT_MAX_TOKENS = 2000

_loaded: dict[str, object] = {}


def log(message: str, level: str = "info") -> None:
    prefix = f"[{TOOL_ID}]" if level == "info" else f"[{TOOL_ID}] {level.upper()}:"
    print(f"{prefix} {message}", file=sys.stderr, flush=True)


def checkpoint_directory() -> str:
    """The *directory* holding ``checkpoint.pt`` — ``load_for_inference`` joins the two itself."""
    return os.environ.get("INFERHUB_BG_TTS_CHECKPOINT") or os.path.join("/data", "tools", "bg-tts-v5")


def checkpoint_present() -> bool:
    return os.path.isfile(os.path.join(checkpoint_directory(), "checkpoint.pt"))


def has_ffmpeg() -> bool:
    return shutil.which("ffmpeg") is not None


def formats() -> tuple[str, ...]:
    return NATIVE_FORMATS + (ENCODED_FORMATS if has_ffmpeg() else ())


def device() -> str:
    try:
        if torch.cuda.is_available():
            return "cuda"
    except Exception as error:  # noqa: BLE001 - a probe must never be why a worker fails to start
        log(f"CUDA probe failed ({type(error).__name__}: {error}); falling back to the CPU", "warning")

    return "cpu"


def offered() -> list[str]:
    return list(SPEAKERS) if checkpoint_present() else []


def load():
    """Load the checkpoint, tokenizer and codec once; both speakers share all three."""
    if "engine" in _loaded:
        return _loaded["engine"]

    if not checkpoint_present():
        raise ToolError(
            f"the bg-tts-v5 checkpoint is not on this box "
            f"(expected {os.path.join(checkpoint_directory(), 'checkpoint.pt')}). Download the "
            f"checkpoint/ directory from https://huggingface.co/beleata74/bg-tts-v5 into "
            f"{checkpoint_directory()}.",
            ERROR_MODEL_UNAVAILABLE,
        )

    where = device()

    if where == "cpu":
        log(
            "no usable CUDA device — loading on the CPU, which this 250M-parameter decoder will "
            "run at a small fraction of the ~1.1x realtime the model card reports on a GPU",
            "warning",
        )

    log(f"loading bg-tts-v5 on {where}")

    # CodecV5 fetches NVIDIA's NanoCodec (`nvidia/nemo-nano-codec-22khz-0.6kbps-12.5fps`, a few
    # hundred MB) through `AudioCodecModel.from_pretrained`, unconditionally — unlike the 2.9 GB
    # checkpoint above, there is no "serve this voice without its own codec" state to gate behind
    # `INFERHUB_ALLOW_MODEL_DOWNLOAD`, so the first load on a fresh box pays this one small fetch
    # regardless. It lands in the same `HF_HOME` cache `rerank_worker.py` already uses.

    with contextlib.redirect_stdout(sys.stderr):
        model = load_for_inference(checkpoint_directory(), device=where)
        tokenizer = TTSTokenizer()
        codec = CodecV5(device=where)

    _loaded["engine"] = (model, tokenizer, codec, where)
    log("bg-tts-v5 is ready")
    return _loaded["engine"]


def synthesise(text: str, speaker_id: int, wav_path: str, options: dict) -> None:
    model, tokenizer, codec, where = load()

    with contextlib.redirect_stdout(sys.stderr):
        tokens = generate(
            model,
            tokenizer,
            text,
            speaker_id=speaker_id,
            max_new_tokens=options["max_tokens"],
            temperature=options["temperature"],
            top_k=options["top_k"],
            top_p=options["top_p"],
            rep_penalty=options["rep_penalty"],
            device=where,
        )

    if tokens is None or len(tokens) == 0:
        raise ToolError("the model produced no audio for this input")

    tokens = tokens[: len(tokens) - len(tokens) % CODEC_NUM_CODEBOOKS]

    with contextlib.redirect_stdout(sys.stderr):
        codec.tokens_to_wav(tokens, wav_path)


def encode(wav_path: str, target: str, fmt: str) -> None:
    result = subprocess.run(
        ["ffmpeg", "-nostdin", "-y", "-loglevel", "error", "-i", wav_path, target],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        raise ToolError(f"ffmpeg could not produce {fmt}: {result.stderr.strip()[:400]}")


def numeric(payload: dict, key: str, default: float, caster) -> object:
    if key not in payload or payload[key] is None:
        return default

    try:
        return caster(payload[key])
    except (TypeError, ValueError):
        raise ToolError(f"'{key}' must be a number", ERROR_INVALID_REQUEST) from None


def speak(request: Request):
    payload = request.payload or {}
    text = payload.get("input") or ""

    if not text:
        raise ToolError("input is required", ERROR_INVALID_REQUEST)

    speed = payload.get("speed")
    if speed not in (None, 1, 1.0):
        raise ToolError(
            "bg-tts-v5 has no speed control (unlike Piper's length_scale) — omit 'speed' or send 1",
            ERROR_INVALID_REQUEST,
        )

    if payload.get("stream_format"):
        raise ToolError("bg-tts-v5 cannot stream; request without 'stream_format'", ERROR_UNSUPPORTED_FORMAT)

    fmt = (payload.get("response_format") or "wav").lower()
    supported = formats()

    if fmt not in supported:
        raise ToolError(
            f"this worker cannot produce '{fmt}' (no ffmpeg on this box). It can produce: "
            f"{', '.join(supported)}",
            ERROR_UNSUPPORTED_FORMAT,
        )

    name = payload.get("voice") or request.model
    speaker_id = SPEAKERS.get(name)

    if speaker_id is None:
        raise ToolError(
            f"voice '{name}' is not one this worker serves. Available: {', '.join(sorted(SPEAKERS))}",
            ERROR_INVALID_REQUEST,
        )

    options = {
        "temperature": numeric(payload, "temperature", DEFAULT_TEMPERATURE, float),
        "top_k": numeric(payload, "top_k", DEFAULT_TOP_K, int),
        "top_p": numeric(payload, "top_p", DEFAULT_TOP_P, float),
        "rep_penalty": numeric(payload, "rep_penalty", DEFAULT_REP_PENALTY, float),
        "max_tokens": numeric(payload, "max_tokens", DEFAULT_MAX_TOKENS, int),
    }

    wav = request.output("speech.wav", "audio/wav")
    synthesise(text, speaker_id, wav.path, options)

    if fmt == "wav":
        log(f"synthesised {len(text)} characters with {name} as wav")
        return {"format": "wav", "voice": name, "characters": len(text)}, [wav]

    if fmt == "pcm":
        raw = request.output("speech.pcm", "audio/pcm")

        with wave.open(wav.path, "rb") as source, open(raw.path, "wb") as target:
            target.write(source.readframes(source.getnframes()))

        log(f"synthesised {len(text)} characters with {name} as pcm")
        return {"format": "pcm", "voice": name, "characters": len(text)}, [raw]

    encoded = request.output(f"speech.{fmt}", {"mp3": "audio/mpeg", "opus": "audio/ogg", "flac": "audio/flac"}[fmt])
    encode(wav.path, encoded.path, fmt)

    log(f"synthesised {len(text)} characters with {name} as {fmt}")
    return {"format": fmt, "voice": name, "characters": len(text)}, [encoded]


def main() -> None:
    available = offered()

    if not available:
        log(
            f"no checkpoint found under {checkpoint_directory()}, so this worker offers nothing. "
            "Download the checkpoint/ directory from https://huggingface.co/beleata74/bg-tts-v5 "
            "into that path."
        )
    else:
        log(f"offering voices: {', '.join(available)} (formats: {', '.join(formats())})")

    Worker(capabilities=[{"kind": "speak", "models": available}]).run(speak)


if __name__ == "__main__":
    main()
