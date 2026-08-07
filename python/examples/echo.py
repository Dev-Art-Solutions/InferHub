#!/usr/bin/env python3
"""The smallest possible InferHub tool worker.

Run it by hand to see the protocol, which is the fastest way to understand it:

    $ python3 examples/echo.py
    {"type":"hello","protocol":1,"tool":"echo"}
    {"type":"ready","protocol":1,"capabilities":[{"kind":"echo","models":["echo"]}]}
    {"type":"request","id":"1","capability":"echo","model":"echo","payload":{"hello":"world"}}
    {"type":"result","id":"1","payload":{"echoed":{"hello":"world"},"model":"echo"}}

The manifest that goes with it:

    {
      "id": "echo",
      "capabilities": [ { "kind": "echo", "models": ["echo"] } ],
      "command": ["/usr/bin/python3", "-u", "/opt/inferhub/tools/echo.py"],
      "maxWorkers": 1
    }

Note ``-u``. Without it Python buffers stdout and the node waits out its start timeout for a
``ready`` frame that is sitting in a buffer. It is the single most common way a first worker
fails, and it looks exactly like a hang.
"""

import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from inferhub_worker import ERROR_MODEL_UNAVAILABLE, File, ToolError, Worker  # noqa: E402


_pulled: set[str] = set()


def handle(request):
    # Phase 48. `{"op": "pull"}` / `{"op": "delete"}` is what a hub-issued model command sends a
    # tool, and the echo worker answers it with the same shape a real one does: a `chunk` per step
    # of progress, then a terminal result. That is what makes the model-command path testable with
    # no GPU, no network and no weights — phase-41's echo-worker discipline, one release on.
    #
    # `{"op": "pull", "fail": "..."}` refuses, because the interesting half of a pull is what a
    # coordinator does with one that does not work.
    op = (request.payload or {}).get("op")

    if op in ("pull", "delete"):
        if request.payload.get("fail"):
            raise ToolError(request.payload["fail"], ERROR_MODEL_UNAVAILABLE)

        if op == "delete":
            _pulled.discard(request.model)
            return {"model": request.model, "status": "deleted"}

        if request.model in _pulled:
            request.chunk({"status": "already-present", "model": request.model})
            return {"model": request.model, "status": "already-present"}

        for landed in (128, 512, 1024):
            time.sleep(int(request.payload.get("stepMs", 5)) / 1000.0)
            request.chunk({"status": "downloading (%d MiB)" % landed, "model": request.model, "mib": landed})

        _pulled.add(request.model)
        request.chunk({"status": "verifying", "model": request.model})

        return {"model": request.model, "status": "ready", "ready": True}

    # Phase 47. `{"behaviour": "slow", "steps": 30, "stepMs": 200}` makes this worker take real time,
    # emit a real progress frame per step and honour a real cancel — which is how the whole async
    # job model (SSE, monotonic progress, cooperative cancel, a worker that stays warm afterwards)
    # can be exercised on a laptop with no GPU and no weights.
    #
    # The two lines inside the loop are exactly the two lines a diffusers worker puts inside
    # `callback_on_step_end`. There is nothing else to it.
    if request.payload.get("behaviour") == "slow":
        steps = int(request.payload.get("steps", 30))
        step_ms = int(request.payload.get("stepMs", 100))

        for step in range(1, steps + 1):
            time.sleep(step_ms / 1000.0)
            request.progress(step, total_steps=steps)
            request.raise_if_cancelled()

        return {"slept": steps * step_ms / 1000.0, "steps": steps}

    payload = {"echoed": request.payload, "model": request.model}

    if not request.files:
        return payload

    # Files arrive as paths and leave as paths; nothing binary crosses the pipe.
    payload["received"] = [{"name": f.name, "bytes": os.path.getsize(f.path)} for f in request.files]

    out = os.path.join(request.scratch, "echo-output.txt")
    with open(out, "w", encoding="utf-8") as handle_out:
        handle_out.write("echoed %d file(s)\n" % len(request.files))

    return payload, [File("echo-output.txt", "text/plain", out)]


if __name__ == "__main__":
    Worker(capabilities=[{"kind": "echo", "models": ["echo"]}]).run(handle)
