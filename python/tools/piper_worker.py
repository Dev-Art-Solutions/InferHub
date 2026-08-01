#!/usr/bin/env python3
"""Text to speech for InferHub, on Piper (phase 42).

    python -u piper_worker.py

Piper is a small ONNX voice that is *comfortable* on a CPU — chosen for the same reason phase 39
shipped a CPU mode: most boxes that will run this do not have a spare card, and a TTS that needs a
GPU is a TTS most people cannot use.

VOICES are ``.onnx`` + ``.onnx.json`` pairs under ``INFERHUB_PIPER_VOICES`` (the image points that
at ``/data/tools/voices``, on the volume, so a fetched voice survives ``docker run``). The model
name a caller sends is the voice file's stem: ``en_US-amy-medium``. ``voice`` in the request body is
accepted as an override for OpenAI-SDK compatibility, and a *named* voice that does not exist is a
refusal listing the ones that do — never a silent substitution, because a caller who asked for one
voice and got another has a product that shipped in the wrong voice.

FORMATS. ``wav`` and ``pcm`` are native. ``mp3``, ``opus`` and ``flac`` need ``ffmpeg``, and on a box
without it this worker **refuses with ``unsupported_format`` naming what it can do** — the edge
turns that into a 400. Returning a wav labelled ``audio/mpeg`` would be a corrupted file with a
confident content type, found three days later in a media player.
"""

from __future__ import annotations

import glob
import json
import os
import shutil
import subprocess
import sys
import wave

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from inferhub_worker import (  # noqa: E402
    ERROR_INVALID_REQUEST,
    ERROR_UNSUPPORTED_FORMAT,
    Request,
    ToolError,
    Worker,
)

TOOL_ID = "piper"

NATIVE_FORMATS = ("wav", "pcm")
ENCODED_FORMATS = ("mp3", "opus", "flac")

_loaded: dict[str, object] = {}


def log(message: str) -> None:
    print(f"[{TOOL_ID}] {message}", file=sys.stderr, flush=True)


def voices_directory() -> str:
    return os.environ.get("INFERHUB_PIPER_VOICES") or os.path.join("/data", "tools", "voices")


def voices() -> dict[str, str]:
    """Voice name → the .onnx path. A voice without its .json sidecar is not a voice."""
    found: dict[str, str] = {}

    for path in sorted(glob.glob(os.path.join(voices_directory(), "**", "*.onnx"), recursive=True)):
        if not os.path.exists(path + ".json"):
            log(f"skipping {os.path.basename(path)}: its .onnx.json config is missing")
            continue

        found[os.path.basename(path)[: -len(".onnx")]] = path

    return found


def has_ffmpeg() -> bool:
    return shutil.which("ffmpeg") is not None


def formats() -> tuple[str, ...]:
    return NATIVE_FORMATS + (ENCODED_FORMATS if has_ffmpeg() else ())


def load(name: str):
    if name in _loaded:
        return _loaded[name]

    available = voices()
    path = available.get(name)

    if path is None:
        raise ToolError(
            f"voice '{name}' is not on this box. Available: {', '.join(sorted(available)) or 'none'}",
            ERROR_INVALID_REQUEST,
        )

    from piper import PiperVoice

    log(f"loading voice {name}")
    _loaded[name] = PiperVoice.load(path, config_path=path + ".json")
    return _loaded[name]


def sample_rate(voice_path: str) -> int:
    with open(voice_path + ".json", "r", encoding="utf-8") as handle:
        return int(json.load(handle).get("audio", {}).get("sample_rate", 22050))


def synthesise(voice, text: str, wav_path: str) -> None:
    """Piper writes a wav; the raw PCM and every encoded format are derived from it."""
    with wave.open(wav_path, "wb") as output:
        voice.synthesize(text, output)


def encode(wav_path: str, target: str, fmt: str) -> None:
    result = subprocess.run(
        ["ffmpeg", "-nostdin", "-y", "-loglevel", "error", "-i", wav_path, target],
        capture_output=True,
        text=True,
    )

    if result.returncode != 0:
        # ffmpeg's own message, not a paraphrase. It names the codec that is missing, which is the
        # one thing that tells an operator whether to install a package or change the format.
        raise ToolError(f"ffmpeg could not produce {fmt}: {result.stderr.strip()[:400]}")


def speak(request: Request):
    payload = request.payload or {}
    text = payload.get("input") or ""

    if not text:
        raise ToolError("input is required", ERROR_INVALID_REQUEST)

    fmt = (payload.get("response_format") or "wav").lower()
    supported = formats()

    if fmt not in supported:
        raise ToolError(
            f"this worker cannot produce '{fmt}' (no ffmpeg on this box). It can produce: "
            f"{', '.join(supported)}",
            ERROR_UNSUPPORTED_FORMAT,
        )

    name = payload.get("voice") or request.model
    voice = load(name)

    wav = request.output("speech.wav", "audio/wav")
    synthesise(voice, text, wav.path)

    if fmt == "wav":
        log(f"synthesised {len(text)} characters with {name} as wav")
        return {"format": "wav", "voice": name, "characters": len(text)}, [wav]

    if fmt == "pcm":
        # Headerless 16-bit little-endian at the voice's own rate. The caller has to know the rate;
        # OpenAI's API has the same hole and every client that asks for pcm already handles it.
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
    available = sorted(voices())

    if not available:
        log(
            f"no voices found under {voices_directory()}, so this worker offers nothing. "
            "Download one .onnx + .onnx.json pair from "
            "https://huggingface.co/rhasspy/piper-voices into that directory."
        )
    else:
        log(f"offering voices: {', '.join(available)} (formats: {', '.join(formats())})")

    Worker(capabilities=[{"kind": "speak", "models": available}]).run(speak)


if __name__ == "__main__":
    main()
