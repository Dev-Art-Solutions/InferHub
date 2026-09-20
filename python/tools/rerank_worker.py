#!/usr/bin/env python3
"""A dedicated cross-encoder reranker for InferHub (phase 80), on sentence-transformers.

    python -u rerank_worker.py

Same shape as every other InferHub tool worker: a child process the node spawns, talks to over
one JSON object per line, and restarts when it dies. It answers, always::

    {"scores": [0.87, 0.12, 0.63]}

one float per document, in the order the caller sent them — no free text to parse, unlike the
existing LLM reranker (``RerankPrompt`` on the .NET side), because a cross-encoder's own output
*is* the score. Ordering the candidates by it is the caller's job (``RerankPrompt.Apply`` is reused
for exactly that on both the hub and a solo node).

REQUEST SHAPE. Not a file: ``request.payload == {"query": "...", "documents": ["...", "..."]}``.
This worker never calls ``request.input_path()`` — there is nothing to upload, which is also why it
needs no dialect-specific HTTP route (unlike transcribe/speak): the generic
``POST /api/tools/rerank`` already speaks JSON in, JSON out.

MODELS. The manifest names which ones this worker may be asked for; at handshake it reports the
ones it can actually serve — a narrowing, never a widening (phase-41 D2), exactly as
``whisper_worker.py`` does. With ``INFERHUB_ALLOW_MODEL_DOWNLOAD=1`` every named model is offered
and the weights are fetched on first use; without it, only models already in the cache are offered.

DEVICE. CUDA when the driver is loadable, CPU otherwise, logged either way (phase-39 D6's lesson).
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

# Imported here, at module scope, on the main thread — not lazily inside `load()` the way
# faster-whisper is in whisper_worker.py. FOUND BY RUNNING THE WORKER, not by reading it: `Worker`
# dispatches a request onto its own thread (v3.16.2's one-reader shape), and the first time this
# import happened there — pulling in numpy's and torch's native extensions for the first time in the
# process — it deadlocked hard inside CPython's import machinery loading numpy's compiled
# `multiarray` module, confirmed with `faulthandler.dump_traceback` against every thread. A plain
# background thread with nothing else going on imports the same modules in a few seconds; the
# combination of a main thread parked in a blocking stdin read plus a second thread pulling in a
# large native extension for the first time is what triggers it — a class of loader-lock hazard,
# not a bug in the scoring logic. Paying the (several-second) import cost once at boot, before
# `Worker.run()` ever spawns a request thread, sidesteps the whole class rather than working around
# one instance of it — and it means `ready` genuinely means ready, not "ready, plus one surprise
# multi-second import on whoever's first request."
from sentence_transformers import CrossEncoder  # noqa: E402

TOOL_ID = "rerank"

#: Model name -> the sentence-transformers CrossEncoder repo it maps to. A caller says
#: `bge-reranker-v2-m3` because that is what the manifest declares; sentence-transformers wants the
#: HF repo id.
MODELS = {
    "ms-marco-minilm-l6": "cross-encoder/ms-marco-MiniLM-L-6-v2",
    "bge-reranker-base": "BAAI/bge-reranker-base",
    "bge-reranker-large": "BAAI/bge-reranker-large",
    "bge-reranker-v2-m3": "BAAI/bge-reranker-v2-m3",
}

#: The manifest names every key above, so this and `python/manifests/rerank.json` have to agree.
#: A name here the manifest does not grant is simply never routed, the safe direction.

_loaded: dict[str, object] = {}


def log(message: str, level: str = "info") -> None:
    prefix = f"[{TOOL_ID}]" if level == "info" else f"[{TOOL_ID}] {level.upper()}:"
    print(f"{prefix} {message}", file=sys.stderr, flush=True)


def allow_download() -> bool:
    return os.environ.get("INFERHUB_ALLOW_MODEL_DOWNLOAD", "").strip().lower() in ("1", "true", "yes")


def cache_root() -> str:
    return os.environ.get("HF_HOME") or os.path.join(os.path.expanduser("~"), ".cache", "huggingface")


def cached(repo: str) -> bool:
    """Whether this repo's snapshot is already on disk, the same directory-name convention every
    huggingface_hub cache uses (`models--{org}--{name}`)."""
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


def device() -> str:
    try:
        import torch

        if torch.cuda.is_available():
            return "cuda"
    except Exception as error:  # noqa: BLE001 - a probe must never be why a worker fails to start
        log(f"CUDA probe failed ({type(error).__name__}: {error}); falling back to the CPU")

    return "cpu"


def load(model: str):
    if model in _loaded:
        return _loaded[model]

    repo = MODELS.get(model)

    if repo is None:
        raise ToolError(f"model '{model}' is not one this worker serves", ERROR_INVALID_REQUEST)

    if not allow_download() and not cached(repo):
        raise ToolError(
            f"the weights for '{model}' are not on this box and downloading is off "
            f"(Tools:AllowModelDownload / INFERHUB_ALLOW_MODEL_DOWNLOAD). Pre-fetch them with: "
            f"docker exec <container> /opt/inferhub/venv/bin/python -c "
            f"\"from sentence_transformers import CrossEncoder; CrossEncoder('{repo}')\"",
            ERROR_MODEL_UNAVAILABLE,
        )

    where = device()
    log(f"loading {model} ({repo}) on {where}")

    try:
        _loaded[model] = CrossEncoder(repo, device=where, local_files_only=not allow_download())
    except Exception as error:  # noqa: BLE001
        if where == "cuda":
            log(
                f"CUDA is visible but unusable ({type(error).__name__}: {error}). "
                "Falling back to the CPU.",
                "warning",
            )
            try:
                _loaded[model] = CrossEncoder(repo, device="cpu", local_files_only=not allow_download())
            except Exception as fallback:  # noqa: BLE001
                raise ToolError(
                    f"could not load '{model}' on the CPU either: {type(fallback).__name__}: {fallback}"
                ) from fallback
        else:
            raise ToolError(f"could not load '{model}': {type(error).__name__}: {error}") from error

    log(f"{model} is ready")
    return _loaded[model]


def rerank(request: Request):
    model = load(request.model)

    payload = request.payload or {}
    query = payload.get("query")
    documents = payload.get("documents")

    if not isinstance(query, str) or not query.strip():
        raise ToolError("payload.query is required and must be a non-empty string", ERROR_INVALID_REQUEST)

    if not isinstance(documents, list) or not documents or not all(isinstance(d, str) for d in documents):
        raise ToolError("payload.documents is required and must be a non-empty list of strings", ERROR_INVALID_REQUEST)

    pairs = [[query, document] for document in documents]
    scores = model.predict(pairs)

    log(f"scored {len(documents)} document(s) against one query with {request.model}")

    return {"scores": [float(score) for score in scores]}


def main() -> None:
    names = [
        name
        for name in MODELS
        if name in (os.environ.get("INFERHUB_RERANK_MODELS") or " ".join(MODELS)).split()
    ]
    available = offered(names)

    if not available:
        log(
            "no reranker weights are present and downloading is off, so this worker offers nothing. "
            "The node will not be routed reranking."
        )
    else:
        log(f"offering: {', '.join(available)}")

    Worker(capabilities=[{"kind": "rerank", "models": available}]).run(rerank)


if __name__ == "__main__":
    main()
