#!/usr/bin/env python3
"""Text to image for InferHub, on diffusers (phase 46).

    python -u diffusion_worker.py

STABLE DIFFUSION, and deliberately nothing heavier in this release. ``sdxl`` is ~7 GB at fp16 and
fits an 8 GB card with no quantization; ``sd15`` is ~2 GB at 512² and is the only recipe that is
honestly usable without a card. FLUX.1-schnell and Qwen-Image are 12B and 20B, neither fits a 24 GB
card at bf16, and both arrive in phase 48 together with the quantization path they need — leading
with a model that cannot run unquantized would have meant shipping the client dialect, the VRAM
budget and bitsandbytes in one release, and the first bug would have had three plausible causes.

A RECIPE IS A MODEL; A MANIFEST IS A TOOL (phase-46 D3). The manifest is the operator's ceiling and
is what ``Tools:Allowed`` names; recipes are a catalogue this worker reads from
``INFERHUB_IMAGE_RECIPES``. Each one pins a Hugging Face repo AND A REVISION — "it worked in 3.14.0"
has to have an answer, and a repo's ``main`` is not one.

SIZES ARE A LIST, NOT A RANGE, and for SDXL that is load-bearing rather than fussy: it was trained on
a fixed set of aspect buckets, and a size outside them does not fail — it produces duplicated limbs
and doubled horizons, which reads as "this model is bad" rather than "you asked for 1000x1000". A
size the recipe does not have is refused with ``invalid_request`` naming the ones it does, and the
edge renders that as a 400 without reading the message.

WEIGHTS ARE NEVER FETCHED INSIDE A REQUEST (v3.14.1, and it is why that release exists). v3.14.0
let ``from_pretrained`` download on first use, inside the manifest's ``requestTimeoutSeconds``: on a
fresh volume the first ``sdxl`` call spent 900 seconds downloading and then returned 502, twice.
So a recipe is only DECLARED once its weights are proven loadable, a background thread does the
fetching, and the worker RE-DECLARES when one lands (``Worker.redeclare``). The fleet therefore
never routes at a model that is not ready, and no caller ever waits on a download.

``variant`` IS NOT ``dtype``, and conflating them cost 7 GB per model in v3.14.0. Passing
``torch_dtype=float16`` makes diffusers download the **fp32** files and cast them in memory; the
repo also carries ``*.fp16.safetensors`` at half the size, and only ``variant="fp16"`` asks for
them. Both are set in the recipes, and a repo without the variant falls back loudly.

THE DEVICE IS LOGGED ON THE FIRST LINE (phase-39 D6). Four gigabytes of CUDA and a silent CPU
fallback is an afternoon of blaming the model. With ``INFERHUB_IMAGE_REQUIRE_GPU=1`` — the default —
this worker refuses to start at all when no CUDA device is reachable, and says which key to unset;
without a GPU only recipes marked ``cpuViable`` are offered, unless
``INFERHUB_IMAGE_ALLOW_SLOW_CPU=1`` says the operator has read both numbers and wants the slow one.

NOTHING IS RETAINED. Images are written into the node's per-request scratch directory and named
back; the node deletes the whole directory in a ``finally``. The prompt is never logged, at any
level, by anything here — it is content in exactly the sense design rule 7 means.
"""

from __future__ import annotations

import glob
import json
import os
import sys
import threading
from typing import Any

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from inferhub_worker import (  # noqa: E402
    ERROR_INVALID_REQUEST,
    ERROR_MODEL_UNAVAILABLE,
    Request,
    ToolError,
    Worker,
)

TOOL_ID = "diffusion"

DEFAULT_RECIPES = "/opt/inferhub/recipes"

# One pipeline at a time. Loading SDXL is tens of seconds, so a worker that reloaded per request
# would spend more time loading than generating; a worker that kept several resident would need the
# VRAM budget that phase 48 adds, and guessing at it here is how a box OOMs at 2am.
_loaded: dict[str, Any] = {}
_device: str = "cpu"
_dtype: Any = None


def log(message: str) -> None:
    """stderr, relayed into the node's log under this tool's id. NEVER the prompt."""
    print(f"[{TOOL_ID}] {message}", file=sys.stderr, flush=True)


def recipes_directory() -> str:
    return os.environ.get("INFERHUB_IMAGE_RECIPES") or DEFAULT_RECIPES


def flag(name: str, default: bool = False) -> bool:
    value = os.environ.get(name)
    return default if value is None or value == "" else value not in ("0", "false", "False")


def load_recipes() -> dict[str, dict[str, Any]]:
    """Every ``*.json`` in the recipe directory. A broken one is skipped and logged, never fatal."""
    found: dict[str, dict[str, Any]] = {}

    for path in sorted(glob.glob(os.path.join(recipes_directory(), "*.json"))):
        try:
            with open(path, encoding="utf-8") as handle:
                recipe = json.load(handle)
        except (OSError, json.JSONDecodeError) as error:
            log(f"skipping {os.path.basename(path)}: {error}")
            continue

        identifier = recipe.get("id")
        if not identifier:
            log(f"skipping {os.path.basename(path)}: no 'id'")
            continue

        # A pin is not optional. Without it "which weights are in 3.14.0" has no answer, and two
        # builds of the same tag can contain different models (phase-39 D9, phase-42 D2).
        if not recipe.get("revision"):
            log(f"skipping {identifier}: 'revision' is required — pin a commit sha, not a branch")
            continue

        found[identifier] = recipe

    return found


def select_device() -> str:
    """``cuda`` when it is reachable, else ``cpu``. Said out loud either way."""
    global _dtype

    try:
        import torch
    except ImportError as error:  # pragma: no cover - the image asserts this at build time
        raise SystemExit(f"[{TOOL_ID}] torch is not importable: {error}") from error

    if torch.cuda.is_available():
        _dtype = torch.float16
        name = torch.cuda.get_device_name(0)
        log(f"device: cuda ({name})")
        return "cuda"

    _dtype = torch.float32

    # The CUDA-is-present-but-unusable case, said plainly. Under WSL2 there are no /dev/nvidia*
    # nodes and the only honest signal is whether the driver library loads, so "no GPU" here can
    # mean a container started without --gpus, a runtime without `compute` in
    # NVIDIA_DRIVER_CAPABILITIES, or genuinely no card. All three look identical from in here.
    log("device: cpu — no CUDA device is reachable (no --gpus, no `compute` capability, or no card)")
    return "cpu"


def offered(recipes: dict[str, dict[str, Any]], device: str) -> list[str]:
    """
    Which recipes this box will actually accept work for.

    A recipe that is not ``cpuViable`` on a CPU-only node is *not declared*, so the hub never routes
    to it — 41 D6's withdraw-on-failure, applied before the first failure rather than after it. An
    operator who wants the four-minute path anyway sets ``Tools:Image:AllowSlowCpu``.
    """
    if device == "cuda" or flag("INFERHUB_IMAGE_ALLOW_SLOW_CPU"):
        return sorted(recipes)

    viable = sorted(identifier for identifier, recipe in recipes.items() if recipe.get("cpuViable"))

    for identifier in sorted(set(recipes) - set(viable)):
        log(
            f"not offering '{identifier}' on a CPU-only node: it is not marked cpuViable. "
            "Set Tools:Image:AllowSlowCpu=true to offer it anyway (minutes per image)."
        )

    return viable


def pipeline_class(name: str):
    import diffusers

    cls = getattr(diffusers, name, None)

    if cls is None:
        raise ToolError(f"diffusers has no pipeline class '{name}'", ERROR_INVALID_REQUEST)

    return cls


# ---- readiness (v3.14.1) -----------------------------------------------------------------------
#
# "Are this recipe's weights here?" has exactly one reliable answer, and it was found by running the
# published v3.14.0 image against a half-downloaded cache:
#
#   snapshot_download(local_files_only=True)      returns OK with the UNet entirely absent — it
#                                                 resolves the snapshot folder and verifies nothing
#   DiffusionPipeline.download(local_files_only=True)   same, equally useless
#   from_pretrained(local_files_only=True)        RAISES, correctly, naming what is missing
#
# Only the third asks the question the next request will ask. But it also loads the model, so it is
# not something to do casually — hence a MARKER FILE: the prefetch proves loadability once, writes
# the marker, and every later boot trusts it. A marker without weights self-heals (the prefetch runs
# again, finds everything cached, and completes in seconds); weights without a marker cost one
# background load. Neither costs a caller anything.


def ready_marker(recipe: dict[str, Any]) -> str:
    home = os.environ.get("HF_HOME") or os.path.join("/data", "tools", "hf")
    name = f"{recipe['id']}-{recipe['revision'][:12]}-{recipe.get('variant') or 'default'}"
    return os.path.join(home, ".inferhub-ready", name)


def is_ready(recipe: dict[str, Any]) -> bool:
    return os.path.exists(ready_marker(recipe))


def mark_ready(recipe: dict[str, Any]) -> None:
    marker = ready_marker(recipe)
    os.makedirs(os.path.dirname(marker), exist_ok=True)

    with open(marker, "w", encoding="utf-8") as handle:
        handle.write(f"{recipe['repo']}@{recipe['revision']}\n")


def prefetch_command(recipe: dict[str, Any]) -> str:
    variant = recipe.get("variant")
    pattern = f' --include "*.{variant}.safetensors" "*.json" "*.txt" "*/*"' if variant else ""
    return f"huggingface-cli download {recipe['repo']} --revision {recipe['revision']}{pattern}"


def fetch(recipe: dict[str, Any]) -> None:
    """
    Download and PROVE loadable, on a background thread. Never called from a request.

    Loading it here rather than only downloading is deliberate: it is the same call the next
    request makes, so "the prefetch succeeded" and "a request will work" cannot diverge. A pattern
    list handed to snapshot_download can be subtly wrong and would move the failure back into
    somebody's request, which is the whole bug this exists to fix.
    """
    pipe = _from_pretrained(recipe, local_files_only=False)
    del pipe

    import torch

    if _device == "cuda":
        torch.cuda.empty_cache()

    mark_ready(recipe)


def _from_pretrained(recipe: dict[str, Any], local_files_only: bool):
    """The one place a pipeline is constructed, so serving and prefetching cannot drift apart."""
    cls = pipeline_class(recipe.get("pipeline", "AutoPipelineForText2Image"))

    kwargs: dict[str, Any] = {
        "revision": recipe["revision"],
        "torch_dtype": _dtype,
        "local_files_only": local_files_only,
    }

    if recipe.get("variant"):
        kwargs["variant"] = recipe["variant"]

    # A safety checker that returns a BLACK IMAGE on a positive is disqualifying: the operator gets
    # a bug report rather than a policy signal, and the failure is indistinguishable from a broken
    # VAE, a bad seed or an OOM. Phase-46 D8 — this box generates what it is asked to generate and
    # the policy is the operator's. Not every pipeline class accepts these two (SDXL does not).
    try:
        return cls.from_pretrained(
            recipe["repo"], safety_checker=None, requires_safety_checker=False, **kwargs
        )
    except TypeError:
        pass
    except ValueError as error:
        if "variant" not in str(error) or not recipe.get("variant"):
            raise

        # The repo has no such variant. Fall back to the default files LOUDLY — silently doubling
        # somebody's download is exactly the class of thing this release exists to stop.
        log(
            f"{recipe['id']}: '{recipe['repo']}' has no '{recipe['variant']}' variant; falling back "
            f"to the default weights, which are roughly twice the size. Remove \"variant\" from the "
            f"recipe to silence this."
        )
        kwargs.pop("variant")
        recipe.pop("variant", None)

    return cls.from_pretrained(recipe["repo"], **kwargs)


def load(recipe: dict[str, Any]):
    identifier = recipe["id"]

    if identifier in _loaded:
        return _loaded[identifier]

    if not is_ready(recipe):
        # Not reachable through routing — an unready recipe is not declared — but a solo caller can
        # race a fetch that is still running, and the honest answer is immediate rather than a
        # request that quietly turns into a 7 GB download.
        raise ToolError(
            f"'{identifier}' is not ready on this node: its weights are still being fetched, or "
            "this node may not fetch them (Tools:AllowModelDownload). Pre-fetch them with:\n"
            f"  {prefetch_command(recipe)}",
            ERROR_MODEL_UNAVAILABLE,
        )

    # One at a time: free the previous pipeline before the next allocation rather than after it,
    # or the peak is both models at once and the box OOMs on the swap rather than on the load.
    for other in list(_loaded):
        log(f"unloading {other}")
        del _loaded[other]

    if _device == "cuda":
        import torch

        torch.cuda.empty_cache()

    log(f"loading {identifier} ({recipe['repo']}@{recipe['revision'][:12]}) on {_device}")

    try:
        # local_files_only: the marker says these are here, so a request must never reach the
        # network. If the marker is wrong, this fails in milliseconds with a clear message instead
        # of spending the request's whole deadline on a download.
        pipe = _from_pretrained(recipe, local_files_only=True)
    except Exception as error:  # noqa: BLE001
        raise ToolError(
            f"'{identifier}' is marked ready but did not load: {error}",
            ERROR_MODEL_UNAVAILABLE,
        ) from error

    pipe = pipe.to(_device)
    pipe.set_progress_bar_config(disable=True)

    _loaded[identifier] = pipe
    log(f"{identifier} is ready on {_device}")
    return pipe


def resolve_size(recipe: dict[str, Any], payload: dict[str, Any]) -> tuple[int, int]:
    sizes = recipe.get("sizes") or []
    default = (recipe.get("defaults") or {}).get("size")

    width = payload.get("width")
    height = payload.get("height")

    if width is None or height is None:
        if not default:
            raise ToolError(f"'{recipe['id']}' has no default size and none was given", ERROR_INVALID_REQUEST)

        width, height = (int(part) for part in str(default).lower().split("x"))

    asked = f"{width}x{height}"

    if sizes and asked not in sizes:
        raise ToolError(
            f"this recipe cannot render {asked}. It was trained on: {', '.join(sizes)}",
            ERROR_INVALID_REQUEST,
        )

    return int(width), int(height)


def generate(request: Request):
    recipes = load_recipes()
    recipe = recipes.get(request.model)

    if recipe is None:
        raise ToolError(
            f"no recipe '{request.model}' on this node. Available: {', '.join(sorted(recipes)) or 'none'}",
            ERROR_INVALID_REQUEST,
        )

    payload = request.payload or {}
    prompt = payload.get("prompt") or ""

    if not prompt:
        raise ToolError("prompt is required", ERROR_INVALID_REQUEST)

    width, height = resolve_size(recipe, payload)
    defaults = recipe.get("defaults") or {}

    steps = int(payload.get("steps") or defaults.get("steps") or 30)
    max_steps = int(recipe.get("maxSteps") or 150)

    if steps > max_steps:
        # Clamped rather than refused: a step count above what a recipe is useful at is a request
        # for more quality, not a malformed one, and the response reports what was actually run so
        # the caller is billed for that and can see it.
        log(f"clamping steps {steps} -> {max_steps} for {request.model}")
        steps = max_steps

    guidance = payload.get("guidance")
    guidance = float(guidance) if guidance is not None else float(defaults.get("guidance", 5.0))

    count = int(payload.get("n") or 1)
    seed = payload.get("seed")

    pipe = load(recipe)

    import torch

    files = []
    images = []

    for index in range(count):
        # Every image in a batch gets its own seed, derived from the caller's when they gave one, so
        # `n=4` returns four different pictures AND each one is individually reproducible. A single
        # generator across the batch would make image 3 unreproducible without also rendering 1 and 2.
        image_seed = int(seed) + index if seed is not None else int(torch.seed() % (2**31))
        generator = torch.Generator(device=_device).manual_seed(image_seed)

        result = pipe(
            prompt=prompt,
            negative_prompt=payload.get("negative_prompt"),
            width=width,
            height=height,
            num_inference_steps=steps,
            guidance_scale=guidance,
            generator=generator,
        )

        output = request.output(f"image-{index}.png", "image/png")
        result.images[0].save(output.path, format="PNG")

        files.append(output)
        images.append({"width": width, "height": height, "steps": steps, "seed": image_seed})

    # The model, the geometry and the seeds. Not the prompt — the node logs this line's *shape*, and
    # a payload that carried the prompt would put it in the one place rule 7 forbids.
    log(f"{request.model}: {count} image(s) at {width}x{height}, {steps} steps on {_device}")

    return {"model": request.model, "steps": steps, "device": _device, "images": images}, files


def prefetch_missing(worker: Worker, recipes: dict[str, dict[str, Any]], offerable: list[str]) -> None:
    """
    Fetch, prove, and RE-DECLARE — on a background thread, never inside a request (v3.14.1).

    Each model that lands re-declares immediately rather than waiting for the batch, because the
    first one is usually the one somebody is waiting for.
    """
    missing = [identifier for identifier in offerable if not is_ready(recipes[identifier])]

    if not missing:
        return

    if not flag("INFERHUB_ALLOW_MODEL_DOWNLOAD"):
        # The third opt-in (phase-42 D4). Refused, named, and with the command to run — a message
        # that only says "no" leaves the operator guessing.
        for identifier in missing:
            log(
                f"not offering '{identifier}': its weights are not on this box and "
                f"Tools:AllowModelDownload is false. Pre-fetch them with:\n"
                f"  {prefetch_command(recipes[identifier])}"
            )
        return

    for identifier in missing:
        recipe = recipes[identifier]

        try:
            log(
                f"fetching weights for '{identifier}' from {recipe['repo']}@{recipe['revision'][:12]} "
                f"(variant={recipe.get('variant') or 'default'}) into {os.environ.get('HF_HOME')}. "
                "This runs in the background; the recipe is offered when it completes."
            )
            fetch(recipe)
        except Exception as error:  # noqa: BLE001 - one bad model must not stop the others
            log(f"could not fetch '{identifier}': {type(error).__name__}: {str(error)[:300]}")
            continue

        ready = [i for i in offerable if is_ready(recipes[i])]
        log(f"'{identifier}' is ready; offering recipes: {', '.join(ready)}")
        worker.redeclare([{"kind": "image", "models": ready}])


def main() -> None:
    global _device

    recipes = load_recipes()

    if not recipes:
        log(f"no recipes found under {recipes_directory()}, so this worker offers nothing")
        Worker(capabilities=[{"kind": "image", "models": []}]).run(generate)
        return

    _device = select_device()

    if _device != "cuda" and flag("INFERHUB_IMAGE_REQUIRE_GPU", default=True):
        # A tool that loads happily on a CPU and then serves four-minute requests is not a slow
        # feature, it is a node the fleet keeps routing to: the hub sees a healthy capability and
        # every caller pays for the discovery. Refusing costs one log line, read by the person who
        # can fix it. Phase-35 D4 vs phase-37 D4, applied to a number.
        raise SystemExit(
            f"[{TOOL_ID}] no CUDA device is reachable and Tools:Image:RequireGpu is true, so this "
            "worker will not start. Set Tools:Image:RequireGpu=false to run on the CPU "
            "(sd15 at 512x512 is tens of seconds; sdxl at 1024x1024 is minutes)."
        )

    offerable = offered(recipes, _device)
    ready = [identifier for identifier in offerable if is_ready(recipes[identifier])]

    log(
        f"offering recipes: {', '.join(ready) or 'none yet'}"
        + (f" (fetching: {', '.join(i for i in offerable if i not in ready)})" if len(ready) < len(offerable) else "")
    )

    worker = Worker(capabilities=[{"kind": "image", "models": ready}])

    # Daemon: a fetch in flight must not keep the process alive past a SIGTERM. An interrupted
    # download resumes from its `.incomplete` blob on the next start, so nothing is lost.
    threading.Thread(
        target=prefetch_missing, args=(worker, recipes, offerable), daemon=True, name="prefetch"
    ).start()

    worker.run(generate)


if __name__ == "__main__":
    main()
