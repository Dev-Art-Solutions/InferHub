"""InferHub tool-worker protocol, for Python workers.

This is a *reference implementation*, deliberately not a package: there is no ``setup.py``, no
PyPI release and no version to depend on. Copy the directory next to your worker, or vendor it.
A published package would be a support surface this repository has not agreed to maintain, and
the whole file is about 150 lines — reading it is faster than reading its changelog would be.

The node speaks a **process protocol, not Python** (phase 41, D1). Nothing on the .NET side knows
this file exists; a worker written in Go, or a shell script wrapping ``ffmpeg``, is exactly as
valid. Python is simply where ``faster-whisper`` and ``piper`` live.

Usage::

    from inferhub_worker import Worker

    def transcribe(request):
        path = request.files[0].path              # bytes arrive as a path, never on the pipe
        request.log("loading %s" % request.model)  # or just print() to stderr
        return {"text": "..."}, None               # (payload, files)

    Worker(capabilities=[{"kind": "transcribe", "models": ["whisper-small"]}]).run(transcribe)

Three rules worth knowing before you write one:

1. **stdout is the protocol.** One JSON object per line and nothing else. If a library you use
   prints to stdout, redirect it — ``contextlib.redirect_stdout(sys.stderr)`` is the usual fix.
   The node ignores non-frame lines rather than dying, but you will not see them where you expect.
2. **stderr is your log**, and it is relayed verbatim into the node's log under your tool's id.
   A traceback is the most useful thing you can leave for whoever is debugging you at 2am.
3. **Binary goes through files.** A request carries paths into a scratch directory the node owns;
   write your outputs into ``request.scratch`` and name them back. The node deletes the whole
   directory when the request ends, whether it succeeded or not.
"""

from __future__ import annotations

import json
import signal
import sys
import traceback
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable, Sequence

PROTOCOL_VERSION = 1

__all__ = ["PROTOCOL_VERSION", "File", "Request", "ToolError", "Worker"]


@dataclass(frozen=True)
class File:
    """A file handed over by path. ``path`` is inside the request's scratch directory."""

    name: str
    media_type: str
    path: str

    def to_frame(self) -> dict[str, str]:
        return {"name": self.name, "mediaType": self.media_type, "path": self.path}


@dataclass
class Request:
    id: str
    capability: str
    model: str
    payload: dict[str, Any]
    files: list[File] = field(default_factory=list)
    scratch: str = ""
    _worker: "Worker | None" = None

    def log(self, message: str, level: str = "info") -> None:
        """Send a structured log line. ``print(..., file=sys.stderr)`` works just as well."""
        if self._worker is not None:
            self._worker._send({"type": "log", "level": level, "message": message})

    def chunk(self, payload: dict[str, Any]) -> None:
        """Emit a partial answer. Only a streaming caller sees these; a blocking one ignores them."""
        if self._worker is not None:
            self._worker._send({"type": "chunk", "id": self.id, "payload": payload})


class ToolError(Exception):
    """Raise this for a failure the caller should see. Anything else is caught and reported too."""


Handler = Callable[[Request], Any]


class Worker:
    """The read-dispatch-write loop. Construct it, hand it a function, and it does the rest."""

    def __init__(
        self,
        capabilities: Sequence[dict[str, Any]] | None = None,
        stdin: Any = None,
        stdout: Any = None,
    ) -> None:
        # Reported at handshake. The node treats it as a NARROWING of the manifest: you may say
        # you found only one of the two models the manifest names, and you may never add one.
        # That is not a formality — the operator's file on the box is the authority on what this
        # node may be asked to do, and a script that could grant itself capabilities would be
        # deciding what traffic the fleet sends it.
        self.capabilities = list(capabilities or [])
        self._stdin = stdin or sys.stdin
        self._stdout = stdout or sys.stdout
        self._running = True

    def run(self, handler: Handler) -> None:
        # SIGTERM is how a node stops a worker it has decided to retire. Exiting cleanly here is
        # what lets a half-written model file get closed.
        try:
            signal.signal(signal.SIGTERM, self._on_terminate)
        except (ValueError, AttributeError, OSError):
            pass  # not the main thread, or Windows

        for line in self._stdin:
            if not self._running:
                break

            line = line.strip()
            if not line:
                continue

            try:
                frame = json.loads(line)
            except json.JSONDecodeError:
                # A line we do not understand is not a reason to die: it is how a newer node
                # talking to an older worker looks.
                continue

            kind = frame.get("type")

            if kind == "hello":
                self._send(
                    {
                        "type": "ready",
                        "protocol": PROTOCOL_VERSION,
                        **({"capabilities": self.capabilities} if self.capabilities else {}),
                    }
                )
            elif kind == "ping":
                self._send({"type": "pong"})
            elif kind == "request":
                self._handle(handler, frame)

    # ---- internals ---------------------------------------------------------------------------

    def _handle(self, handler: Handler, frame: dict[str, Any]) -> None:
        request = Request(
            id=frame.get("id") or "",
            capability=frame.get("capability") or "",
            model=frame.get("model") or "",
            payload=frame.get("payload") or {},
            files=[
                File(f.get("name", ""), f.get("mediaType", "application/octet-stream"), f["path"])
                for f in (frame.get("files") or [])
            ],
            scratch=frame.get("scratch") or "",
            _worker=self,
        )

        try:
            answer = handler(request)
        except ToolError as error:
            self._send({"type": "error", "id": request.id, "message": str(error)})
            return
        except Exception as error:  # noqa: BLE001 - a worker must never die on one bad request
            traceback.print_exc(file=sys.stderr)
            self._send({"type": "error", "id": request.id, "message": f"{type(error).__name__}: {error}"})
            return

        payload, files = _split(answer)
        result: dict[str, Any] = {"type": "result", "id": request.id, "payload": payload}

        if files:
            result["files"] = [f.to_frame() for f in files]

        self._send(result)

    def _send(self, frame: dict[str, Any]) -> None:
        # separators without spaces, and no indentation: one object, one line, always.
        self._stdout.write(json.dumps(frame, separators=(",", ":")) + "\n")
        self._stdout.flush()

    def _on_terminate(self, *_: Any) -> None:
        self._running = False


def _split(answer: Any) -> tuple[dict[str, Any], list[File]]:
    """A handler may return a payload, or ``(payload, files)``."""
    if isinstance(answer, tuple):
        payload, files = answer
        return payload or {}, list(files or [])

    if isinstance(answer, Iterable) and not isinstance(answer, (dict, str, bytes)):
        raise ToolError("a handler must return a dict, or a (dict, files) tuple")

    return answer or {}, []
