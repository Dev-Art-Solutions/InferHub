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

FETCHED VOICES (phase 90). ``INFERHUB_SPEECH_VOICES`` (``Tools:Speech:Voices`` on the node) names
voices from the catalogue in ``INFERHUB_PIPER_CATALOGUE`` — ``python/voices/``, one pinned file per
voice — and a named voice that is not on the volume is fetched on a background thread, **verified
against its sha256 before it is renamed into place**, and declared when it lands. Nothing is fetched
unless it is named: a default voice would be a default *language*, and that is still not ours to pick.
A name that is not in the catalogue is refused by name; the manual route (drop a pair in) still works
for every voice that is not.
"""

from __future__ import annotations

import base64
import glob
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import urllib.request
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

# Phase 90. The voice ids the background fetch has not finished with yet.
_fetching: set[str] = set()


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


def capability_frames() -> list[dict]:
    return [{"kind": "speak", "models": sorted(voices())}]


# --- phase 90: the catalogue and the fetch ----------------------------------------------------------

# A voice id becomes a file name on the volume, so it is held to the shape Piper's own ids have
# (`bg_BG-dimitar-medium`). Anything with a separator or a dot in it is refused before it is used
# to build a path — the node refuses it at startup too, and this is the lock on the process that
# would actually write the file.
_VOICE_ID = re.compile(r"^[A-Za-z0-9_-]+$")

_FETCH_BLOCK = 1024 * 1024


def catalogue_directory() -> str:
    return os.environ.get("INFERHUB_PIPER_CATALOGUE") or os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "voices"
    )


def requested_voices() -> list[str]:
    """``INFERHUB_SPEECH_VOICES``, comma-separated, in order, blanks and repeats dropped."""
    seen: list[str] = []

    for entry in (os.environ.get("INFERHUB_SPEECH_VOICES") or "").split(","):
        entry = entry.strip()

        if entry and entry not in seen:
            seen.append(entry)

    return seen


def endpoint() -> str:
    # HF_ENDPOINT is huggingface_hub's own name for a mirror, so an operator behind one sets the
    # variable they already know. It reaches this process only through the manifest's `env`: the
    # node clears the environment before spawn.
    return (os.environ.get("HF_ENDPOINT") or "https://huggingface.co").rstrip("/")


def catalogue_entry(voice_id: str) -> dict:
    """The pinned entry for one voice, or a ValueError whose message is the log line."""
    if not _VOICE_ID.match(voice_id):
        raise ValueError(f"'{voice_id}' is not a voice id (letters, digits, '_' and '-' only)")

    path = os.path.join(catalogue_directory(), voice_id + ".json")

    if not os.path.isfile(path):
        known = sorted(
            os.path.basename(p)[: -len(".json")] for p in glob.glob(os.path.join(catalogue_directory(), "*.json"))
        )
        raise ValueError(
            f"'{voice_id}' is not in the voice catalogue at {catalogue_directory()} "
            f"(it has: {', '.join(known) or 'nothing'}). A voice that is not catalogued can still be "
            f"used by dropping its .onnx + .onnx.json pair into {voices_directory()}"
        )

    with open(path, encoding="utf-8") as handle:
        entry = json.load(handle)

    files = entry.get("files") or []
    names = sorted(os.path.basename(f.get("path") or "") for f in files)

    # The pair, and only the pair: the worker declares a voice by finding exactly these two names,
    # so an entry that lands anything else would land a voice under a name nobody asked for.
    if entry.get("id") != voice_id or names != sorted([voice_id + ".onnx", voice_id + ".onnx.json"]):
        raise ValueError(f"catalogue entry {path} must describe '{voice_id}.onnx' and '{voice_id}.onnx.json'")

    if not re.match(r"^[0-9a-f]{40}$", entry.get("revision") or ""):
        raise ValueError(f"catalogue entry {path} has no pinned revision (a 40-character commit sha)")

    for f in files:
        if not re.match(r"^[0-9a-f]{64}$", f.get("sha256") or "") or not isinstance(f.get("bytes"), int):
            raise ValueError(f"catalogue entry {path}: every file needs a sha256 and a byte count")

    return entry


def fetch_voice(entry: dict, target: str | None = None) -> None:
    """
    Download, verify, then rename into place — the ``.onnx.json`` last.

    Nothing is renamed until every file has matched its pinned size and sha256, so a truncated or
    substituted download never becomes a voice. And the config goes last because ``voices()``
    declares a voice by finding the pair: a restart in the middle sees an ``.onnx`` with no sidecar,
    which it already skips and says so.
    """
    target = target or voices_directory()
    os.makedirs(target, exist_ok=True)
    staged: list[tuple[str, str]] = []

    try:
        for f in sorted(entry["files"], key=lambda f: f["path"].endswith(".json")):
            name = os.path.basename(f["path"])
            part = os.path.join(target, f".{name}.part")
            url = f"{endpoint()}/{entry['repo']}/resolve/{entry['revision']}/{f['path']}"
            digest = hashlib.sha256()
            size = 0

            with urllib.request.urlopen(url, timeout=60) as response, open(part, "wb") as out:
                while block := response.read(_FETCH_BLOCK):
                    digest.update(block)
                    size += len(block)
                    out.write(block)

            staged.append((part, os.path.join(target, name)))

            if size != f["bytes"] or digest.hexdigest() != f["sha256"]:
                raise ValueError(
                    f"{name} from {url} is {size} bytes with sha256 {digest.hexdigest()}; "
                    f"the catalogue pins {f['bytes']} bytes and {f['sha256']}. Not installed"
                )

        for part, final in staged:
            os.replace(part, final)
    finally:
        for part, _ in staged:
            if os.path.exists(part):
                os.remove(part)


def plan_fetches() -> list[dict]:
    """The catalogue entries to fetch now. Every voice that will not be fetched is logged, by name."""
    wanted = requested_voices()
    present = voices()
    plan: list[dict] = []

    for voice_id in wanted:
        if voice_id in present:
            continue

        try:
            entry = catalogue_entry(voice_id)
        except (ValueError, OSError) as error:
            log(f"not fetching voice {voice_id}: {error}")
            continue

        if os.environ.get("INFERHUB_ALLOW_MODEL_DOWNLOAD") != "1":
            # The third opt-in (phase-42 D4). Naming a voice says WHICH; this says whether the box
            # may reach the internet at all, and an air-gapped operator's answer wins.
            log(
                f"not fetching voice {voice_id}: Tools:AllowModelDownload is false. Put its two files "
                f"into {voices_directory()} by hand: "
                + ", ".join(f"{endpoint()}/{entry['repo']}/resolve/{entry['revision']}/{f['path']}" for f in entry["files"])
            )
            continue

        plan.append(entry)

    return plan


def fetch_missing(worker: Worker, plan: list[dict]) -> None:
    """One voice at a time, re-declaring as each lands or fails (the diffusion worker's shape)."""
    for index, entry in enumerate(plan):
        remaining = [e["id"] for e in plan[index + 1:]]
        megabytes = sum(f["bytes"] for f in entry["files"]) / 1e6

        log(
            f"fetching voice {entry['id']} ({entry.get('language', '?')}, {megabytes:.0f} MB, "
            f"licence {entry.get('license', {}).get('id', '?')}) from {entry['repo']}@{entry['revision'][:12]}; "
            "it is offered when it has landed and matched its pinned sha256"
        )

        try:
            fetch_voice(entry)
            log(f"voice {entry['id']} is ready")
        except Exception as error:  # noqa: BLE001 - one bad voice must not stop the others
            log(f"could not fetch voice {entry['id']}: {error}")

        _fetching.discard(entry["id"])
        worker.redeclare(capability_frames(), fetching=remaining)


def has_ffmpeg() -> bool:
    return shutil.which("ffmpeg") is not None


def formats() -> tuple[str, ...]:
    return NATIVE_FORMATS + (ENCODED_FORMATS if has_ffmpeg() else ())


def load(name: str):
    if name in _loaded:
        return _loaded[name]

    available = voices()
    path = available.get(name)

    if path is None and name in _fetching:
        # Only a solo caller naming it outright gets here: the voice is not declared until it has
        # landed, so a hub never routes to it early.
        raise ToolError(f"voice '{name}' is still being fetched on this box; try again shortly", ERROR_INVALID_REQUEST)

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
    plan = plan_fetches()

    if not available and not plan:
        log(
            f"no voices found under {voices_directory()}, so this worker offers nothing. "
            "Name one from the catalogue in Tools:Speech:Voices (e.g. bg_BG-dimitar-medium), or "
            "download one .onnx + .onnx.json pair from https://huggingface.co/rhasspy/piper-voices "
            "into that directory."
        )
    else:
        log(
            f"offering voices: {', '.join(available) or 'none yet'} (formats: {', '.join(formats())})"
            + (f"; fetching: {', '.join(e['id'] for e in plan)}" if plan else "")
        )

    _fetching.update(e["id"] for e in plan)
    worker = Worker(capabilities=capability_frames(), fetching=[e["id"] for e in plan])

    if plan:
        # Daemon, as the diffusion prefetch is: a fetch in flight must not keep the process alive
        # past a SIGTERM, and a half-written `.part` is never mistaken for a voice.
        threading.Thread(target=fetch_missing, args=(worker, plan), daemon=True, name="voice-fetch").start()

    worker.run(speak)


if __name__ == "__main__":
    main()
