#!/usr/bin/env python3
"""Speech to text for InferHub, on faster-whisper (phase 42).

    python -u whisper_worker.py

It is an ordinary InferHub tool worker: a child process the node spawns, talks to over one JSON
object per line, and restarts when it dies. Nothing on the .NET side knows this file is Python
(phase-41 D1) — it knows how to start a process, write a line and read a line.

What it answers with, always, whatever the caller asked for::

    {"text": "...", "language": "en", "duration": 12.3,
     "segments": [{"id": 0, "start": 0.0, "end": 3.2, "text": "..."}]}

The edge turns that into ``json``, ``text``, ``srt``, ``vtt`` or ``verbose_json``. A worker author
never writes an SRT timestamp, and two workers cannot disagree about where the comma goes.

MODELS. The manifest names which ones this worker may be asked for; at handshake it reports the
ones it can actually serve — a *narrowing*, never a widening (phase-41 D2: the operator's file is
the authority on what this node may be asked to do). With ``INFERHUB_ALLOW_MODEL_DOWNLOAD=1`` every
named model is offered and the weights are fetched on first use; without it, only models already in
the cache are offered, so the fleet never routes a job at a box that would have to reach the
internet to answer it.

DEVICE. CUDA when the driver is loadable, CPU otherwise, and **it logs which** in its first lines.
Four gigabytes of CUDA runtime, a dropped ``--gpus`` flag and a silent CPU fallback is an afternoon
spent blaming the model (phase-39 D6); the log line is the whole mitigation.
"""

from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from inferhub_worker import (  # noqa: E402
    ERROR_INVALID_REQUEST,
    ERROR_MODEL_UNAVAILABLE,
    Request,
    ToolError,
    Worker,
)

TOOL_ID = "whisper"

#: Model name → the faster-whisper size/repo it maps to. A caller says `whisper-small` because that
#: is what `/v1/models` reports; CTranslate2 wants `small`.
MODELS = {
    "whisper-tiny": "tiny",
    "whisper-base": "base",
    "whisper-small": "small",
    "whisper-medium": "medium",
    "whisper-large-v3": "large-v3",
    "whisper-large-v3-turbo": "large-v3-turbo",
}

#: The manifest names every key above, so this and `python/manifests/whisper.json` have to agree.
#: They are checked against each other at handshake by nothing at all — a name here that the
#: manifest does not grant is simply never routed, which is the safe direction.

_loaded: dict[str, object] = {}


def log(message: str, level: str = "info") -> None:
    # stderr is not protocol: the node pumps it into its own log under this tool's id, which is
    # where a Python traceback goes and the most useful thing a tool author ever leaves behind.
    prefix = f"[{TOOL_ID}]" if level == "info" else f"[{TOOL_ID}] {level.upper()}:"
    print(f"{prefix} {message}", file=sys.stderr, flush=True)


def allow_download() -> bool:
    return os.environ.get("INFERHUB_ALLOW_MODEL_DOWNLOAD", "").strip().lower() in ("1", "true", "yes")


def cache_root() -> str:
    return os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface")


#: int8 on the CPU is roughly two to three times real time for `small` on a modern desktop core,
#: and the quality difference against float32 is not audible in a transcript.
CPU = ("cpu", "int8")


def device() -> tuple[str, str]:
    """
    (device, compute_type). CUDA when the driver loads, CPU otherwise — never a device node check
    (phase-39 D5: under WSL2 the ``/dev/nvidia*`` nodes do not exist and CUDA works fine).
    """
    try:
        import ctranslate2

        if ctranslate2.get_cuda_device_count() > 0:
            return "cuda", "float16"
    except Exception as error:  # noqa: BLE001 - a probe must never be why a worker fails to start
        log(f"CUDA probe failed ({type(error).__name__}: {error}); falling back to the CPU")

    return CPU


def cached(size: str) -> bool:
    """
    Whether the weights for this size are already on disk.

    It asks **faster-whisper's own name → repo map** rather than assembling a cache directory name
    by hand: the repos are not all under one owner (`large-v3-turbo` is `mobiuslabsgmbh/…`, the rest
    are `Systran/…`), so a hand-built `models--Systran--faster-whisper-{size}` would report "not
    cached" for a model that is sitting right there — and with downloads off that means a node
    silently refuses to offer a capability it has.
    """
    try:
        from faster_whisper.utils import _MODELS
    except Exception:  # noqa: BLE001 - a probe must never be why a worker fails to start
        return False

    repo = _MODELS.get(size)

    if repo is None:
        return False

    root = cache_root()
    needle = "models--" + repo.replace("/", "--")

    if not os.path.isdir(root):
        return False

    for current, directories, _ in os.walk(root):
        if needle in directories or os.path.basename(current) == needle:
            return True

    return False


def offered(names: list[str]) -> list[str]:
    if allow_download():
        return names

    return [name for name in names if cached(MODELS[name])]


def load(model: str):
    if model in _loaded:
        return _loaded[model]

    size = MODELS.get(model)

    if size is None:
        raise ToolError(f"model '{model}' is not one this worker serves", ERROR_INVALID_REQUEST)

    if not allow_download() and not cached(size):
        # Named flag, exact command. Phase-36 D6's discipline: an operator who is told "download is
        # off" and nothing else has to go and find out what the command was.
        raise ToolError(
            f"the weights for '{model}' are not on this box and downloading is off "
            f"(Tools:AllowModelDownload / INFERHUB_ALLOW_MODEL_DOWNLOAD). Pre-fetch them with: "
            f"docker exec <container> /opt/inferhub/venv/bin/python -c "
            f"\"from faster_whisper import WhisperModel; WhisperModel('{size}')\"",
            ERROR_MODEL_UNAVAILABLE,
        )

    from faster_whisper import WhisperModel

    def build(where: str, compute: str):
        # local_files_only mirrors the consent into the library itself, so a bug in the cache check
        # above cannot turn "downloads are off" into a silent download. The flag is the contract;
        # this makes it enforceable rather than advisory.
        return WhisperModel(
            size,
            device=where,
            compute_type=compute,
            local_files_only=not allow_download(),
        )

    where, compute = device()
    log(f"loading {model} ({size}) on {where}/{compute}")

    try:
        _loaded[model] = build(where, compute)
    except Exception as error:  # noqa: BLE001
        # FOUND BY RUNNING THE IMAGE. `get_cuda_device_count()` counts what the *driver* can see;
        # CTranslate2 additionally needs the CUDA *runtime* — libcublas, libcudart — and a box that
        # has the first and not the second fails here with "Library libcublas.so.12 is not found".
        # A card that cannot be used is the operator's problem to fix and a *slow* box in the
        # meantime; a failed job is everyone's, and it is not the trade phase-39 D6 made. So the
        # fallback is automatic and the log says exactly why, at Warning, every time.
        if where == "cuda":
            log(
                f"CUDA is visible but unusable ({type(error).__name__}: {error}). "
                "Falling back to the CPU — transcription will work and will be several times "
                "slower. On a bare-metal node, put your CUDA runtime on the worker's "
                "LD_LIBRARY_PATH via the manifest's 'env'.",
                "warning",
            )

            try:
                where, compute = CPU
                _loaded[model] = build(where, compute)
            except Exception as fallback:  # noqa: BLE001
                raise ToolError(
                    f"could not load '{model}' on the CPU either: {type(fallback).__name__}: {fallback}"
                ) from fallback
        else:
            raise ToolError(f"could not load '{model}': {type(error).__name__}: {error}") from error

    log(f"{model} is ready on {where}")
    return _loaded[model]


def transcribe(request: Request):
    path = request.input_path()
    model = load(request.model)

    payload = request.payload or {}
    language = payload.get("language") or None
    prompt = payload.get("prompt") or None
    temperature = payload.get("temperature")

    segments, info = model.transcribe(
        path,
        language=language,
        initial_prompt=prompt,
        temperature=float(temperature) if temperature is not None else 0.0,
        vad_filter=False,
        # Explicit, because the library's default flipped to True and the edge builds SRT and
        # WebVTT cues out of these timings. A subtitle file whose cues are all approximately right
        # is the failure nobody reports as a bug — it just looks like a bad transcription.
        without_timestamps=False,
    )

    # faster-whisper yields lazily; the transcription does not actually happen until this loop.
    rendered = []
    text = []

    for index, segment in enumerate(segments):
        rendered.append(
            {
                "id": index,
                "start": round(segment.start, 3),
                "end": round(segment.end, 3),
                "text": segment.text,
            }
        )
        text.append(segment.text)

    # Duration, not a transcript: this is the number that reaches the usage ledger (D7), and it is
    # measured off the decoded file rather than derived from the upload's byte count, which a
    # variable-bitrate encoding would make a guess.
    duration = round(float(getattr(info, "duration", 0.0) or 0.0), 3)
    log(f"transcribed {duration:.1f}s of audio with {request.model} ({len(rendered)} segments)")

    return {
        "text": "".join(text).strip(),
        "language": getattr(info, "language", None) or language,
        "duration": duration,
        "segments": rendered,
    }


def main() -> None:
    names = [name for name in MODELS if name in (os.environ.get("INFERHUB_WHISPER_MODELS") or " ".join(MODELS)).split()]
    available = offered(names)

    if not available:
        log(
            "no Whisper weights are present and downloading is off, so this worker offers nothing. "
            "The node will not be routed transcriptions."
        )
    else:
        log(f"offering: {', '.join(available)}")

    Worker(capabilities=[{"kind": "transcribe", "models": available}]).run(transcribe)


if __name__ == "__main__":
    main()
