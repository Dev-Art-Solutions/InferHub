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
import os
import signal
import sys
import threading
import traceback
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable, Sequence

PROTOCOL_VERSION = 1

__all__ = [
    "PROTOCOL_VERSION",
    "ERROR_INVALID_REQUEST",
    "ERROR_UNSUPPORTED_FORMAT",
    "ERROR_MODEL_UNAVAILABLE",
    "ERROR_CANCELLED",
    "Cancelled",
    "File",
    "Request",
    "ToolError",
    "Worker",
]

# Error codes the node understands (phase 42). A code is how a worker says which *kind* of failure
# this was, so the HTTP edge can pick a status without reading the message: the two below marked
# "client" become a 400, everything else stays a 502. Without them, "this box has no ffmpeg so I
# cannot make you an mp3" reaches the caller as a server error and they file a bug about the server.
ERROR_INVALID_REQUEST = "invalid_request"
ERROR_UNSUPPORTED_FORMAT = "unsupported_format"
ERROR_MODEL_UNAVAILABLE = "model_unavailable"

# Phase 47. Neither a client error nor a server one: the job ends `cancelled`, nothing is metered,
# and — this is the part that matters — YOUR PROCESS STAYS ALIVE. The node asked you to stop so it
# would not have to kill you, because killing you throws away weights that took a minute to load and
# the next caller pays for it.
ERROR_CANCELLED = "cancelled"


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
    _cancelled: threading.Event = field(default_factory=threading.Event)
    _step: int = 0

    def log(self, message: str, level: str = "info") -> None:
        """Send a structured log line. ``print(..., file=sys.stderr)`` works just as well."""
        if self._worker is not None:
            self._worker._send({"type": "log", "level": level, "message": message})

    def chunk(self, payload: dict[str, Any]) -> None:
        """Emit a partial answer. Only a streaming caller sees these; a blocking one ignores them."""
        if self._worker is not None:
            self._worker._send({"type": "chunk", "id": self.id, "payload": payload})

    # ---- progress and cancel (phase 47) --------------------------------------------------------

    def progress(self, step: int, total_steps: int | None = None) -> None:
        """
        Report a step (1-based). Costs a callback and nothing else, so emit one per step.

        In ``diffusers`` this is one line inside ``callback_on_step_end``::

            def on_step(pipe, step, timestep, kwargs):
                request.progress(step + 1, total_steps=steps)
                request.raise_if_cancelled()
                return kwargs

        Do NOT decode the latents here to send a preview image. That costs a VAE decode per step —
        10-15% of the run — and produces intermediate *content*, which nothing in InferHub retains
        by design.
        """
        self._step = int(step)

        if self._worker is not None:
            self._worker._send(
                {"type": "progress", "id": self.id, "step": int(step), "totalSteps": total_steps}
            )

    @property
    def cancelled(self) -> bool:
        """Whether the node has asked for this request to stop."""
        return self._cancelled.is_set()

    def raise_if_cancelled(self) -> None:
        """
        Raise :class:`Cancelled` if a cancel has arrived. Call it once per step.

        Everything about cancellation is cooperative and the deadline is real: past
        ``Tools:CancelGraceSeconds`` (20s by default) the node terminates the process and restarts
        it, and the next caller pays the weight-load. A worker that checks once per step is never
        anywhere near that; one that checks only between requests cannot be cancelled at all, which
        is the most common way a first cancellable worker is written wrong and looks exactly like a
        hang.
        """
        if self._cancelled.is_set():
            raise Cancelled(f"cancelled at step {self._step}")

    # ---- file handoff (phase 42) ---------------------------------------------------------------
    #
    # Binary never travels on the pipe. Inputs arrive as paths in ``files``; outputs are written
    # into ``scratch`` and named back. The node deletes the whole directory when the request ends —
    # success or failure — so nothing here has to clean up after itself, and nothing here may write
    # anywhere else: a worker that names a file outside its scratch directory is refused and logged,
    # because reading it would turn "a tool ran" into "a tool exfiltrated a file through the
    # client-facing API".

    def input_path(self, index: int = 0) -> str:
        """The path of the n-th uploaded file. Raises ToolError when the caller sent none."""
        if index >= len(self.files):
            raise ToolError("this request carried no file")

        return self.files[index].path

    def output(self, name: str, media_type: str = "application/octet-stream") -> File:
        """
        A file to write into, inside the scratch directory.

        Create it, write to ``result.path``, and return it (or a list of them) alongside your
        payload. ``name`` is what the caller sees; anything that looks like a path in it is dropped,
        so a media type from a request can never become a directory traversal.
        """
        safe = os.path.basename(name) or "output"
        return File(safe, media_type, os.path.join(self.scratch, safe))


class Cancelled(Exception):
    """
    Raised by :meth:`Request.raise_if_cancelled`, and caught by the worker loop.

    It becomes an ``error`` frame coded ``cancelled``. Let it propagate: catching it to "clean up
    and return a partial result" would hand the caller half an image with a 200 on it.
    """


class ToolError(Exception):
    """
    Raise this for a failure the caller should see. Anything else is caught and reported too.

    ``code`` is optional and is one of the ``ERROR_*`` constants above. Use it when the failure is
    the *request's* fault — an unproducible format, a voice that does not exist — so the caller gets
    a 400 with your sentence in it rather than a 502 that reads as "the server is broken".
    """

    def __init__(self, message: str, code: str | None = None) -> None:
        super().__init__(message)
        self.code = code


Handler = Callable[[Request], Any]


class Worker:
    """The read-dispatch-write loop. Construct it, hand it a function, and it does the rest."""

    def __init__(
        self,
        capabilities: Sequence[dict[str, Any]] | None = None,
        stdin: Any = None,
        stdout: Any = None,
        on_idle: Callable[[], None] | None = None,
        vram_total_mib: int | None = None,
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

        # stdout is the protocol and one frame is one line, so a background thread emitting a
        # `ready` (see redeclare) must not interleave with the request loop's `result`.
        self._write_lock = threading.Lock()

        # Phase 47. The requests currently running, so a `cancel` frame arriving mid-request can
        # find one. A worker that handles requests on the read loop cannot be cancelled at all —
        # it is not reading while it is working — so a request runs on its own thread and the loop
        # stays free.
        self._in_flight: dict[str, Request] = {}
        self._in_flight_lock = threading.Lock()

        # Phase 48. The node sends `idle` when nothing has asked this pool for anything in
        # `idleTimeoutSeconds`, and WHAT TO DO ABOUT IT IS YOURS. The node knows nothing about your
        # libraries — a node-side unload would be the node reaching into a tool's internals — so it
        # states the fact and you decide whether you hold a gigabyte of weights worth freeing. Do
        # not exit here: the process staying alive is the point, because a warm interpreter that
        # reloads weights is much cheaper than a cold start that also re-imports torch.
        self._on_idle = on_idle

        # Phase 48. Reported on `ready` as a CROSS-CHECK, never as a source of truth: the operator
        # declares `Node:Vram:BudgetMiB` and that is what the node's admission gate uses. The node
        # logs the two side by side when they disagree materially, which is the whole value — a node
        # that adopted this number would be detecting VRAM after all, and would be wrong on a shared
        # card or on a box where somebody else's process already holds half of it.
        self._vram_total_mib = vram_total_mib

    def redeclare(self, capabilities: Sequence[dict[str, Any]]) -> None:
        """
        Report a NEW capability set, at any time (v3.14.1).

        A worker whose answer to "what can you do" changes while it runs — weights that finished
        downloading, a voice that appeared on the volume — sends a fresh ``ready`` and the node
        picks it up on its next liveness ping (within ``Tools:MaintenanceInterval``, 30s by
        default) or on the next request, re-narrows against the manifest, and re-reports the node
        to its coordinator. No restart, and no request ever waits for the change.

        The narrowing rule is unchanged and is still enforced on the node: this may only ever
        report a SUBSET of what the manifest granted. Widening is refused there, not trusted here.
        """
        self.capabilities = list(capabilities)
        self._send({"type": "ready", "protocol": PROTOCOL_VERSION, "capabilities": self.capabilities})

    def run(self, handler: Handler) -> None:
        # SIGTERM is how a node stops a worker it has decided to retire. Exiting cleanly here is
        # what lets a half-written model file get closed.
        try:
            signal.signal(signal.SIGTERM, self._on_terminate)
        except (ValueError, AttributeError, OSError):
            pass  # not the main thread, or Windows

        # ONE READER, ALWAYS, and this is the shape to keep (v3.16.2).
        #
        # v3.15 had two: the loop below read a request, and a second "control pump" read from the
        # same stream while that request ran, honouring `cancel` and `ping` and DISCARDING anything
        # else. The frame it discarded was the next `request` — so on a worker that finishes
        # quickly, every other job vanished and hung until its deadline. It shipped in the v3.16.0
        # diffusion image, where it dropped every second image generation.
        #
        # The reader never blocks on a request now: a request runs on its own thread and this loop
        # goes straight back to reading, so a `cancel` still arrives mid-request. One-at-a-time is
        # kept by joining the previous thread before starting the next, which means a frame is
        # never read and thrown away. `readline()` rather than `for line in self._stdin`, still:
        # the iterator protocol keeps a read-ahead buffer nothing else can see.
        worker_thread: threading.Thread | None = None

        while self._running:
            line = self._stdin.readline()

            if not line:
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
                        **({"vramTotalMiB": self._vram_total_mib} if self._vram_total_mib else {}),
                    }
                )
            elif kind == "ping":
                self._send({"type": "pong"})
            elif kind == "cancel":
                self._cancel(frame.get("id") or "")
            elif kind == "idle":
                self._idle()
            elif kind == "request":
                # The node sends one at a time (its pool provides the concurrency, not this loop),
                # so this join is belt and braces — but it is what makes the contract true here
                # rather than assumed of the caller.
                if worker_thread is not None and worker_thread.is_alive():
                    worker_thread.join()

                worker_thread = threading.Thread(
                    target=self._handle, args=(handler, frame), daemon=True
                )
                worker_thread.start()

        if worker_thread is not None and worker_thread.is_alive():
            # stdin closed: let a cooperative request finish writing its result before we go.
            worker_thread.join(timeout=5)

    # ---- internals ---------------------------------------------------------------------------

    def _idle(self) -> None:
        """An `idle` hint is advice, so a handler that throws is logged and forgotten."""
        if self._on_idle is None:
            return

        try:
            self._on_idle()
        except Exception:  # noqa: BLE001 - freeing memory must never kill a healthy worker
            traceback.print_exc(file=sys.stderr)

    def _cancel(self, request_id: str) -> None:
        with self._in_flight_lock:
            request = self._in_flight.get(request_id)

        if request is not None:
            request._cancelled.set()

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

        with self._in_flight_lock:
            self._in_flight[request.id] = request

        try:
            self._dispatch(handler, request)
        finally:
            with self._in_flight_lock:
                self._in_flight.pop(request.id, None)

    def _dispatch(self, handler: Handler, request: Request) -> None:
        try:
            answer = handler(request)
        except Cancelled as cancellation:
            # The process stays alive and keeps its weights. That is the entire point.
            self._send(
                {
                    "type": "error",
                    "id": request.id,
                    "code": ERROR_CANCELLED,
                    "message": str(cancellation) or "the job was cancelled",
                }
            )
            return
        except ToolError as error:
            frame: dict[str, Any] = {"type": "error", "id": request.id, "message": str(error)}

            if getattr(error, "code", None):
                frame["code"] = error.code

            self._send(frame)
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
        # separators without spaces, and no indentation: one object, one line, always. The lock is
        # what keeps that true once `redeclare` can be called from a background thread.
        with self._write_lock:
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
