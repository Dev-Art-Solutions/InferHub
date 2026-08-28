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

STREAMING (phase 70). With ``stream_format`` in the payload the answer arrives as ``chunk`` frames
of base64 **raw PCM** instead of one file: ``PiperVoice.synthesize()`` yields one piece per
sentence, and this splits those at ``_CHUNK_BYTES`` before sending. Two things are deliberate.
**The samples go out headerless whatever the caller asked for** — the wav header is 44 bytes the
edge writes once from the rate reported on the first chunk (D4), because only the edge knows
whether the caller asked for ``wav`` or ``pcm`` and only the first chunk knows the rate. And
**the split is ours rather than the sentence's**: a chunk that crosses the node's wire limit does
not fail the message, it takes the node's connection down (D2), and "one sentence" is not a size —
a caller may send four hundred words without a full stop in them.
"""

from __future__ import annotations

import base64
import glob
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
STREAMABLE_FORMATS = ("wav", "pcm")

# 16 KiB of PCM: ~0.37 s at 22.05 kHz 16-bit mono, and ~21.8 KB once base64 and the frame are on
# it — under SignalR's own 32 KB default, so a hub whose operator never raised a limit is safe. The
# node enforces its own ceiling (ToolProtocol.MaxChunkPayloadBytes) and would fail the job rather
# than let an oversized frame kill its connection; this is the number that keeps that from
# happening.
_CHUNK_BYTES = 16 * 1024

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


def synthesise(voice, text: str, wav_path: str, speed: float | None) -> None:
    """
    Piper writes a wav; the raw PCM and every encoded format are derived from it.

    ``synthesize_wav`` sets the sample rate, width and channel count from the first chunk itself,
    which is why nothing here reads the voice's config to do it — a hand-set rate that disagrees
    with the model produces a file that plays at the wrong pitch and passes every byte-count
    assertion anyone writes.
    """
    from piper import SynthesisConfig

    # length_scale is phoneme duration: < 1 is faster. OpenAI's `speed` is the reciprocal.
    config = SynthesisConfig(length_scale=1.0 / speed) if speed else None

    with wave.open(wav_path, "wb") as output:
        voice.synthesize_wav(text, output, syn_config=config)


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


def stream(request: Request, voice, name: str, text: str, speed) -> dict:
    """
    Emit the answer as it is made, one ``chunk`` frame per ``_CHUNK_BYTES`` of PCM.

    ``synthesize`` yields one ``AudioChunk`` per sentence and each one carries its own measured
    rate, width and channel count — the same three numbers ``synthesize_wav`` reads off the first
    chunk to set a wav's format. They ride on **every** frame rather than only the first, so the
    edge can refuse a worker that changes its mind mid-answer instead of concatenating two rates
    into a file that plays at the wrong speed for half its length.
    """
    from piper import SynthesisConfig

    config = SynthesisConfig(length_scale=1.0 / speed) if speed else None
    pending = b""
    shape: tuple[int, int, int] | None = None
    total = 0

    def emit(samples: bytes) -> None:
        nonlocal total
        request.chunk(
            {
                "audio": base64.b64encode(samples).decode("ascii"),
                "sampleRate": shape[0],
                "sampleWidth": shape[1],
                "channels": shape[2],
            }
        )
        total += len(samples)

    for piece in voice.synthesize(text, syn_config=config):
        # Once per sentence is often enough: the grace is 20s and a sentence is under a second.
        request.raise_if_cancelled()

        shape = (piece.sample_rate, piece.sample_width, piece.sample_channels)
        pending += piece.audio_int16_bytes

        while len(pending) >= _CHUNK_BYTES:
            emit(pending[:_CHUNK_BYTES])
            pending = pending[_CHUNK_BYTES:]

    if pending:
        emit(pending)

    if shape is None:
        # Nothing came back at all. Said here rather than left as an empty stream, which the edge
        # would have to guess about.
        raise ToolError(f"voice '{name}' produced no audio for this input")

    log(f"streamed {len(text)} characters with {name} as {total} bytes of pcm at {shape[0]} Hz")
    return {"format": "pcm", "voice": name, "characters": len(text), "bytes": total, "stream": True}


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

    streaming = payload.get("stream_format")

    if streaming and fmt not in STREAMABLE_FORMATS:
        # The edge refuses this too and gets there first on /v1/audio/speech. It is repeated here
        # because /api/tools/speak forwards a payload verbatim, and a worker that only works when
        # somebody else validated for it is a worker with a hole in it.
        raise ToolError(
            f"'{fmt}' cannot be streamed. This worker streams: {', '.join(STREAMABLE_FORMATS)}",
            ERROR_UNSUPPORTED_FORMAT,
        )

    name = payload.get("voice") or request.model
    voice = load(name)

    if streaming:
        return stream(request, voice, name, text, payload.get("speed"))

    wav = request.output("speech.wav", "audio/wav")
    synthesise(voice, text, wav.path, payload.get("speed"))

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
