#!/usr/bin/env python3
"""Text to image for InferHub, on diffusers (phase 46, catalogued in phase 48).

    python -u diffusion_worker.py

SIX RECIPES, AND FOUR OF THEM NEED SOMETHING PHASE 46 DID NOT HAVE. ``sdxl`` and ``sd15`` run at
fp16 on any card this project has ever asked for. ``flux-schnell`` is 12B and ``qwen-image`` is 20B
plus an 8.3B text encoder — *neither fits a 24 GB card at bf16*, and nf4 quantization is what makes
them one-card models. ``sd35-medium`` and ``sdxl-turbo`` fit fine and need a LICENCE DECISION that
is not ours to make. See ``../recipes/README.md``.

A RECIPE IS A MODEL; A MANIFEST IS A TOOL (phase-46 D3). The manifest is the operator's ceiling and
is what ``Tools:Allowed`` names; recipes are a catalogue this worker reads from
``INFERHUB_IMAGE_RECIPES``. Each one pins a Hugging Face repo AND A REVISION — "it worked in 3.16.0"
has to have an answer, and a repo's ``main`` is not one.

SIZES ARE A LIST, NOT A RANGE, and for SDXL that is load-bearing rather than fussy: it was trained on
a fixed set of aspect buckets, and a size outside them does not fail — it produces duplicated limbs
and doubled horizons, which reads as "this model is bad" rather than "you asked for 1000x1000". A
size the recipe does not have is refused with ``invalid_request`` naming the ones it does, and the
edge renders that as a 400 without reading the message.

WEIGHTS ARE NEVER FETCHED INSIDE A GENERATE REQUEST (v3.14.1, and it is why that release exists).
v3.14.0 let ``from_pretrained`` download on first use, inside the manifest's
``requestTimeoutSeconds``: on a fresh volume the first ``sdxl`` call spent 900 seconds downloading
and then returned 502, twice. So a recipe is only DECLARED once its weights are proven loadable, a
background thread does the fetching, and the worker RE-DECLARES when one lands
(``Worker.redeclare``). Phase 48 adds the other half: a ``pull`` op an operator drives from the hub,
which reports progress on the phase-26 model-command channel instead of guessing when to start.

``variant`` IS NOT ``dtype``, and conflating them cost 7 GB per model in v3.14.0. Passing
``torch_dtype=float16`` makes diffusers download the **fp32** files and cast them in memory; a repo
that carries ``*.fp16.safetensors`` at half the size only hands them over for ``variant="fp16"``.
Both are per-recipe fields, and a repo without the variant falls back loudly.

QUANTIZATION IS A PROPERTY OF THE RECIPE, NOT OF THE REQUEST (phase-48 D6). Two requests to
``qwen-image`` that quantized differently would produce different images from the same seed, and a
per-request knob would make reproducibility a function of a header nobody logged. An operator who
wants both ships two recipes with two ids.

ONE PIPELINE AT A TIME BY DEFAULT, SWAPPED INSIDE A WARM PROCESS (phase-48 D3). Loading FLUX is
40-90 s and a pool that restarted the process per recipe would pay that on every alternation, plus
the interpreter and the import of torch. ``INFERHUB_IMAGE_RESIDENT_RECIPES`` allows more than one
resident on a box with the VRAM for it; the default is 1 because the expensive default is the one
nobody realises they chose.

THE DEVICE IS LOGGED ON THE FIRST LINE (phase-39 D6). Four gigabytes of CUDA and a silent CPU
fallback is an afternoon of blaming the model. With ``INFERHUB_IMAGE_REQUIRE_GPU=1`` — the default —
this worker refuses to start at all when no CUDA device is reachable, and says which key to unset;
without a GPU only recipes marked ``cpuViable`` are offered, unless
``INFERHUB_IMAGE_ALLOW_SLOW_CPU=1`` says the operator has read both numbers and wants the slow one.

ADAPTERS ARE A RECIPE FIELD, AND A RECIPE WITH ADAPTERS IS A DISTINCT MODEL ID (phase-49 D1).
``qwen-image`` and ``qwen-360`` are two ids over one base, not one model with a flag: the router
keys on ``(capability, model)`` and a client asking for ``qwen-image`` must never receive a
panorama. Swapping only the LoRA on a base that is already resident is seconds rather than a minute,
and it is an optimisation this worker makes — invisible to the contract, reported in the timing.

A PROJECTION IS A DECLARED PROPERTY OF A RESULT (phase-49 D4), and a flat recipe says ``flat``
rather than omitting it. A 2048x1024 panorama and a 2048x1024 landscape photograph are the same
bytes in the same shape and are two different pictures; every client guesses from the aspect ratio
today, and it is wrong for every 2:1 photo.

EDITING IS A SECOND CAPABILITY KIND, NOT A FLAG ON THE FIRST (phase-50 D1). A recipe declares
``operations`` and this worker declares ``image`` for the ones that generate and ``image-edit`` for
the ones that edit — because the router keys on ``(kind, model)`` and nothing else, and it is a real
distinction: FLUX.1-schnell has no official inpainting pipeline and SDXL does. A client asking to
edit with a recipe that only generates is refused by name.

OPENAI'S MASK CONVENTION IS INVERTED FROM THE LIBRARY'S (phase-50 D2), and this is the one place in
the whole track where a picture is worth more than a paragraph. OpenAI's edits API treats FULLY
TRANSPARENT pixels as the area to edit; ``diffusers`` takes a mask where WHITE is the area to
inpaint. These are opposite. Everybody gets it wrong once, and the failure mode is "it edited
everything except what I selected", which reads as a broken model. THE CONVERSION HAPPENS HERE
rather than at the edge, because converting one to the other means reading an alpha channel — and
nothing in InferHub's C# ever decodes a pixel (phase-46 D6). A mask with no alpha channel under the
OpenAI convention is refused rather than guessed at: a fully opaque "mask" means "edit nothing",
which no caller ever intends, and reading it as "edit everything" would be a silent substitution of
the most destructive possible interpretation.

STRENGTH IS REPORTED AS THE STEPS THAT ACTUALLY RAN (phase-50 D3). ``diffusers`` starts an
image-to-image run partway down the schedule, so ``strength: 0.6`` at 30 steps denoises for 18 of
them. Metering the asked-for 30 would bill for work nobody did, which is phase-42 D7's rule about
the unit the work is in, applied to a knob rather than a modality.

THE SEAM IS MEASURED AND REPORTED ALWAYS, AND REPAIRED ONLY BECAUSE SOMEBODY ASKED (phase-49 D5,
amended in phase 55). An equirectangular render whose left edge does not continue into its right
edge is not a failure anybody can see — it is a panorama that wraps wrongly, and the person who
finds out is wearing a headset three days later. So the mean absolute difference between the first
and last columns is reported on every panorama, and over the node's threshold it is a WARNING rather
than a failed job: a visible seam is the operator's own aesthetic judgement, and failing the job
would be the tool overriding the person. What phase 55 added is the ASKING: a request that carries
``seam_repair`` gets one, up to the ceiling ``INFERHUB_IMAGE_SEAM_REPAIR`` sets. Nothing repairs by
default and no threshold ever triggers a repair — a number that decides to spend somebody's GPU is
the same overriding, wearing a helpful expression. A repair that did not lower the number is
DISCARDED and both numbers are reported.

NOTHING IS RETAINED. Images are written into the node's per-request scratch directory and named
back; the node deletes the whole directory in a ``finally``. The prompt is never logged, at any
level, by anything here — it is content in exactly the sense design rule 7 means. The recipe's
TRIGGER PHRASE is not: it is a constant of the model, it is the answer to "why does this not look
like a panorama", and a diagnosis nobody can see is not one.
"""

from __future__ import annotations

import glob
import json
import os
import sys
import threading
import time
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

# What is on the card right now, most-recently-used last. Bounded by RESIDENT_RECIPES; the node's
# VramBudget knows the same numbers from the same recipe files and admits work against them, but
# this dict is the truth about this process and it is what the result frame reports back.
_loaded: dict[str, Any] = {}
_loaded_lock = threading.RLock()

# The base each resident pipeline was built from, so a recipe that differs from a resident one only
# by its adapters can take the pipeline over instead of loading a second copy of a 20B transformer.
_base_of: dict[str, tuple] = {}

_device: str = "cpu"

# What this box would offer if every recipe's weights were present — set once at startup, after the
# licence and CPU gates. `pull` and `delete` re-declare against it so the fleet learns about a model
# that has just landed (or just left) without waiting for a restart.
_offerable: list[str] = []
_worker: Worker | None = None


def log(message: str) -> None:
    """stderr, relayed into the node's log under this tool's id. NEVER the prompt."""
    print(f"[{TOOL_ID}] {message}", file=sys.stderr, flush=True)


def recipes_directory() -> str:
    return os.environ.get("INFERHUB_IMAGE_RECIPES") or DEFAULT_RECIPES


def flag(name: str, default: bool = False) -> bool:
    value = os.environ.get(name)
    return default if value is None or value == "" else value not in ("0", "false", "False")


def resident_limit() -> int:
    try:
        return max(1, int(os.environ.get("INFERHUB_IMAGE_RESIDENT_RECIPES") or "1"))
    except ValueError:
        return 1


def seam_warn_threshold() -> float:
    """
    ``Tools:Image:SeamWarnThreshold``. Invariant parsing, always — a decimal comma here would be a
    threshold nobody typed, on exactly the hosts nobody runs CI on.
    """
    try:
        return float(os.environ.get("INFERHUB_IMAGE_SEAM_WARN_THRESHOLD") or "0.08")
    except ValueError:
        log("INFERHUB_IMAGE_SEAM_WARN_THRESHOLD is not a number; using 0.08")
        return 0.08


SEAM_REPAIR_OFF = "off"
SEAM_REPAIR_BLEND = "blend"
SEAM_REPAIR_DIFFUSE = "diffuse"
SEAM_REPAIR_ANY = "any"

SEAM_REPAIR_MECHANISMS = (SEAM_REPAIR_BLEND, SEAM_REPAIR_DIFFUSE)


def seam_repair_ceiling() -> str:
    """
    ``Tools:Image:SeamRepair`` — what an OPERATOR permits, which the request then chooses within.

    Two gates, in phase-41 D2's shape: the node's key is a ceiling the caller can never raise, the
    same way ``Tools:Allowed`` is one the hub can never raise. An unreadable value is read as
    ``off``: the safe direction for a key whose other values spend a GPU.
    """
    value = (os.environ.get("INFERHUB_IMAGE_SEAM_REPAIR") or SEAM_REPAIR_OFF).strip().lower()

    if value in (SEAM_REPAIR_OFF, SEAM_REPAIR_BLEND, SEAM_REPAIR_DIFFUSE, SEAM_REPAIR_ANY):
        return value

    log(f"INFERHUB_IMAGE_SEAM_REPAIR is '{value}', which is not a mode; repair is off")
    return SEAM_REPAIR_OFF


def accepted_licenses() -> set[str]:
    """
    ``Tools:Image:AcceptedLicenses``, lower-cased.

    A BLANK ENTRY IS IGNORED, NOT COUNTED. An array that arrives from a container's environment
    cannot have an element *removed* — ``-e Tools__Image__AcceptedLicenses__0=`` is the only lever
    ``docker run`` gives you — and a blank that matched a licence with a blank id would be a grant
    nobody typed. Same bug the v3.10.0 note records for ``Tools:Allowed``.
    """
    raw = os.environ.get("INFERHUB_IMAGE_ACCEPTED_LICENSES") or ""
    return {entry.strip().lower() for entry in raw.split(",") if entry.strip()}


# ---- the catalogue -----------------------------------------------------------------------------


def licence_of(recipe: dict[str, Any]) -> dict[str, Any]:
    licence = recipe.get("license")
    return licence if isinstance(licence, dict) else {}


def is_permissive(recipe: dict[str, Any]) -> bool:
    """
    Absent metadata is treated as NOT permissive, on purpose.

    A recipe that forgot to say is a recipe nobody has read the licence of, and defaulting to
    "permissive" would make the consent opt-out by accident of a missing field.
    """
    return licence_of(recipe).get("permissive") is True


def licence_id(recipe: dict[str, Any]) -> str:
    return str(licence_of(recipe).get("id") or "unknown").strip()


def is_licensed(recipe: dict[str, Any], accepted: set[str]) -> bool:
    return is_permissive(recipe) or licence_id(recipe).lower() in accepted


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

        # A pin is not optional. Without it "which weights are in 3.16.0" has no answer, and two
        # builds of the same tag can contain different models (phase-39 D9, phase-42 D2).
        if not recipe.get("revision"):
            log(f"skipping {identifier}: 'revision' is required — pin a commit sha, not a branch")
            continue

        found[identifier] = recipe

    return found


_vram_total_mib: int | None = None


def select_device() -> str:
    """``cuda`` when it is reachable, else ``cpu``. Said out loud either way."""
    global _vram_total_mib

    try:
        import torch
    except ImportError as error:  # pragma: no cover - the image asserts this at build time
        raise SystemExit(f"[{TOOL_ID}] torch is not importable: {error}") from error

    if torch.cuda.is_available():
        name = torch.cuda.get_device_name(0)
        free, total = torch.cuda.mem_get_info()
        _vram_total_mib = total // (1024 ** 2)
        log(f"device: cuda ({name}), {free // (1024 ** 2)} MiB free of {_vram_total_mib} MiB")
        return "cuda"

    # The CUDA-is-present-but-unusable case, said plainly. Under WSL2 there are no /dev/nvidia*
    # nodes and the only honest signal is whether the driver library loads, so "no GPU" here can
    # mean a container started without --gpus, a runtime without `compute` in
    # NVIDIA_DRIVER_CAPABILITIES, or genuinely no card. All three look identical from in here.
    log("device: cpu — no CUDA device is reachable (no --gpus, no `compute` capability, or no card)")
    return "cpu"


def dtype_of(recipe: dict[str, Any]):
    """
    The recipe's ``dtype``, honoured. FLUX, SD 3.5 and Qwen-Image are bf16 models and loading them
    at fp16 produces black images on some cards rather than an error — the exact failure phase-46
    D8 refuses to ship for a different reason.
    """
    import torch

    if _device != "cuda":
        return torch.float32

    return {
        "float16": torch.float16,
        "fp16": torch.float16,
        "bfloat16": torch.bfloat16,
        "bf16": torch.bfloat16,
        "float32": torch.float32,
    }.get(str(recipe.get("dtype") or "float16").lower(), torch.float16)


def offered(recipes: dict[str, dict[str, Any]], device: str) -> list[str]:
    """
    Which recipes this box will actually accept work for, before readiness is considered.

    Two gates, and both are refusals rather than warnings because the cost lands on somebody else:
    a recipe that is not ``cpuViable`` on a CPU-only node is *not declared*, so the hub never routes
    to it (41 D6's withdraw-on-failure, applied before the first failure), and a recipe whose
    licence the operator has not accepted is not declared either — accepting a licence on somebody's
    behalf is the one thing in this phase that is definitely not ours to do.
    """
    accepted = accepted_licenses()
    viable: list[str] = []

    for identifier in sorted(recipes):
        recipe = recipes[identifier]

        if not is_licensed(recipe, accepted):
            log(
                f"not offering '{identifier}': its licence is '{licence_id(recipe)}', which is not "
                f"permissive and is not in Tools:Image:AcceptedLicenses. Read it at "
                f"{licence_of(recipe).get('url') or 'the model repository'} and, if you accept it, "
                f"add \"{licence_id(recipe)}\" to that list."
            )
            continue

        if device != "cuda" and not recipe.get("cpuViable") and not flag("INFERHUB_IMAGE_ALLOW_SLOW_CPU"):
            log(
                f"not offering '{identifier}' on a CPU-only node: it is not marked cpuViable. "
                "Set Tools:Image:AllowSlowCpu=true to offer it anyway (minutes per image)."
            )
            continue

        viable.append(identifier)

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


def hf_home() -> str:
    return os.environ.get("HF_HOME") or os.path.join("/data", "tools", "hf")


def adapters_of(recipe: dict[str, Any]) -> list[dict[str, Any]]:
    adapters = recipe.get("adapters")
    return [entry for entry in adapters if isinstance(entry, dict) and entry.get("repo")] if isinstance(adapters, list) else []


def adapter_fingerprint(recipe: dict[str, Any]) -> str:
    """
    Part of the readiness marker, so a recipe whose adapter revision moved is re-proved rather than
    trusted. The base is unchanged and already cached, so being wrong here costs seconds — but
    trusting a marker written for a *different* LoRA would serve the wrong model under the right id.
    """
    adapters = adapters_of(recipe)

    if not adapters:
        return "noadapters"

    return "+".join(f"{a['repo'].split('/')[-1]}@{str(a.get('revision') or 'main')[:8]}" for a in adapters)


def ready_marker(recipe: dict[str, Any]) -> str:
    name = "-".join(
        [
            recipe["id"],
            recipe["revision"][:12],
            recipe.get("variant") or "default",
            str(recipe.get("quantization") or "none"),
            adapter_fingerprint(recipe),
        ]
    )
    return os.path.join(hf_home(), ".inferhub-ready", name)


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
    command = f"huggingface-cli download {recipe['repo']} --revision {recipe['revision']}{pattern}"

    # An adapter is a second repository and a second download. A pre-fetch command that named only
    # the base would look complete and leave the recipe unloadable, which is the worst shape an
    # instruction can have.
    for adapter in adapters_of(recipe):
        weight = f" {adapter['weightFile']}" if adapter.get("weightFile") else ""
        command += (
            f"\n  huggingface-cli download {adapter['repo']} "
            f"--revision {adapter.get('revision') or 'main'}{weight}"
        )

    return command


def fetch(recipe: dict[str, Any]) -> None:
    """
    Download and PROVE loadable. Never called from a generate request.

    Loading it here rather than only downloading is deliberate: it is the same call the next
    request makes, so "the prefetch succeeded" and "a request will work" cannot diverge. A pattern
    list handed to snapshot_download can be subtly wrong and would move the failure back into
    somebody's request, which is the whole bug this exists to fix.
    """
    pipe = _from_pretrained(recipe, local_files_only=False)
    del pipe

    free_vram()
    mark_ready(recipe)


def free_vram() -> None:
    import torch

    if _device == "cuda":
        torch.cuda.empty_cache()


# ---- loading -----------------------------------------------------------------------------------


def quantization_config(recipe: dict[str, Any]):
    """
    ``bitsandbytes`` nf4/int8 through ``diffusers``' native integration (phase-48 D6).

    ONE MECHANISM, DELIBERATELY. GGUF, Nunchaku and TensorRT are all faster on some model on some
    card, and each is a second thing to reason about when an image comes out worse than expected.
    One path means one answer to "why does this look like that".
    """
    mode = str(recipe.get("quantization") or "none").lower()

    if mode in ("", "none"):
        return None

    if mode not in ("nf4", "int8"):
        raise ToolError(
            f"'{recipe['id']}' asks for quantization '{mode}'; this worker does nf4, int8 or none",
            ERROR_INVALID_REQUEST,
        )

    import torch
    from diffusers import PipelineQuantizationConfig

    components = list(recipe.get("quantizeComponents") or ["transformer"])

    if mode == "nf4":
        backend = "bitsandbytes_4bit"
        kwargs = {
            "load_in_4bit": True,
            "bnb_4bit_quant_type": "nf4",
            "bnb_4bit_compute_dtype": dtype_of(recipe) if _device == "cuda" else torch.float32,
        }
    else:
        backend = "bitsandbytes_8bit"
        kwargs = {"load_in_8bit": True}

    return PipelineQuantizationConfig(
        quant_backend=backend,
        quant_kwargs=kwargs,
        components_to_quantize=components,
    )


def apply_adapters(pipe, recipe: dict[str, Any], local_files_only: bool) -> list[str]:
    """
    Stack the recipe's LoRAs onto a pipeline, replacing whatever was on it (phase-49 D1).

    ``unload_lora_weights`` first, unconditionally, because this is also the swap path: a base that
    is already resident under another id still carries that id's adapter, and loading a second one
    on top would silently produce a *blend* of two models under one name — a picture that is neither
    what was asked for nor obviously wrong.

    The names are the recipe's own, so ``set_adapters`` addresses exactly what was loaded. A LoRA
    whose keys do not match the base fails HERE, at load, which is why the prefetch does it too:
    finding that out inside somebody's request is the v3.14.0 failure with a different cause.
    """
    try:
        pipe.unload_lora_weights()
    except (AttributeError, ValueError):
        # A pipeline class with no LoRA support, or one with nothing loaded. Both are fine; a
        # recipe with adapters against a class that cannot take them fails loudly two lines down.
        pass

    adapters = adapters_of(recipe)

    if not adapters:
        return []

    names: list[str] = []
    scales: list[float] = []

    for index, adapter in enumerate(adapters):
        name = str(adapter.get("name") or f"adapter{index}")
        kwargs: dict[str, Any] = {"adapter_name": name, "local_files_only": local_files_only}

        if adapter.get("weightFile"):
            kwargs["weight_name"] = adapter["weightFile"]

        if adapter.get("revision"):
            kwargs["revision"] = adapter["revision"]

        pipe.load_lora_weights(adapter["repo"], **kwargs)
        names.append(name)
        scales.append(float(adapter.get("scale", 1.0)))

    pipe.set_adapters(names, adapter_weights=scales)
    log(f"{recipe['id']}: adapters {', '.join(f'{n}@{s}' for n, s in zip(names, scales))}")
    return names


def base_key(recipe: dict[str, Any]) -> tuple:
    """
    What two recipes must share for one's loaded pipeline to be reusable for the other: the same
    weights, at the same revision, quantized and cast the same way. Everything a LoRA sits on top of.
    """
    return (
        recipe["repo"],
        recipe["revision"],
        str(recipe.get("variant") or ""),
        str(recipe.get("dtype") or ""),
        str(recipe.get("quantization") or "none"),
    )


def _from_pretrained(recipe: dict[str, Any], local_files_only: bool):
    """The one place a pipeline is constructed, so serving and prefetching cannot drift apart."""
    cls = pipeline_class(recipe.get("pipeline", "AutoPipelineForText2Image"))

    kwargs: dict[str, Any] = {
        "revision": recipe["revision"],
        "torch_dtype": dtype_of(recipe),
        "local_files_only": local_files_only,
    }

    if recipe.get("variant"):
        kwargs["variant"] = recipe["variant"]

    quantization = quantization_config(recipe)

    if quantization is not None:
        kwargs["quantization_config"] = quantization

    # A safety checker that returns a BLACK IMAGE on a positive is disqualifying: the operator gets
    # a bug report rather than a policy signal, and the failure is indistinguishable from a broken
    # VAE, a bad seed or an OOM. Phase-46 D8 — this box generates what it is asked to generate and
    # the policy is the operator's. Not every pipeline class accepts these two (SDXL does not).
    pipe = None

    try:
        pipe = cls.from_pretrained(
            recipe["repo"], safety_checker=None, requires_safety_checker=False, **kwargs
        )
    except TypeError:
        pass
    except ValueError as error:
        if "variant" not in str(error) or not recipe.get("variant"):
            raise

        # The repo has no such variant. Fall back to the default files LOUDLY — silently doubling
        # somebody's download is exactly the class of thing v3.14.1 exists to stop.
        log(
            f"{recipe['id']}: '{recipe['repo']}' has no '{recipe['variant']}' variant; falling back "
            f"to the default weights, which are roughly twice the size. Remove \"variant\" from the "
            f"recipe to silence this."
        )
        kwargs.pop("variant")
        recipe.pop("variant", None)

    if pipe is None:
        pipe = cls.from_pretrained(recipe["repo"], **kwargs)

    # Adapters here rather than at the call sites: this is the one place a pipeline is constructed,
    # so the prefetch downloads and PROVES the LoRA exactly as a request will load it. A recipe
    # whose adapter is missing or mismatched is never declared, rather than failing on first use.
    apply_adapters(pipe, recipe, local_files_only)

    return pipe


def evict_for(identifier: str) -> list[str]:
    """
    Make room for one more resident, least-recently-used first.

    Freed BEFORE the next allocation rather than after it: otherwise the peak is both models at
    once and the box goes out of memory on the swap rather than on the load, which is a much harder
    failure to read.
    """
    evicted: list[str] = []
    limit = resident_limit()

    with _loaded_lock:
        while len(_loaded) >= limit and _loaded:
            oldest = next(iter(_loaded))

            if oldest == identifier:
                break

            log(f"unloading {oldest} to make room for {identifier}")
            del _loaded[oldest]
            _base_of.pop(oldest, None)
            evicted.append(oldest)

    if evicted:
        free_vram()

    return evicted


def donor_for(recipe: dict[str, Any]) -> str | None:
    """
    A resident recipe whose pipeline this one can take over: same base, different adapters.

    Least-recently-used first, so a swap costs whichever resident was least likely to be asked for
    next — the same order `evict_for` would have chosen anyway, which is the point: this is an
    eviction that happens to be cheap, not an extra one.
    """
    wanted = base_key(recipe)

    with _loaded_lock:
        for identifier in _loaded:
            if identifier != recipe["id"] and _base_of.get(identifier) == wanted:
                return identifier

    return None


def load(recipe: dict[str, Any], accepted: set[str]) -> tuple[Any, float, list[str]]:
    """Returns (pipeline, load milliseconds, evicted recipe ids)."""
    identifier = recipe["id"]

    # Defence in depth (phase-48 D5). The node refuses to declare an unlicensed recipe and the hub
    # therefore never routes at one; this is the second lock, on the process that would actually
    # download and run the weights. A solo caller naming the recipe directly hits it here.
    if not is_licensed(recipe, accepted):
        raise ToolError(
            f"'{identifier}' is licensed under '{licence_id(recipe)}', which is not permissive. "
            f"This node has not accepted it: add \"{licence_id(recipe)}\" to "
            f"Tools:Image:AcceptedLicenses if you have read it "
            f"({licence_of(recipe).get('url') or 'see the model repository'}).",
            ERROR_MODEL_UNAVAILABLE,
        )

    with _loaded_lock:
        if identifier in _loaded:
            # Touch it: the residency map is ordered least-recently-used first, and dict insertion
            # order is what makes that true without a second structure.
            pipe = _loaded.pop(identifier)
            _loaded[identifier] = pipe
            return pipe, 0.0, []

    if not is_ready(recipe):
        # Not reachable through routing — an unready recipe is not declared — but a solo caller can
        # race a fetch that is still running, and the honest answer is immediate rather than a
        # request that quietly turns into a 20 GB download.
        raise ToolError(
            f"'{identifier}' is not ready on this node: its weights are still being fetched, or "
            "this node may not fetch them (Tools:AllowModelDownload). Pull them from the hub with "
            "POST /api/admin/nodes/{nodeId}/tools/diffusion/models/"
            f"{identifier}/pull, or pre-fetch them with:\n  {prefetch_command(recipe)}",
            ERROR_MODEL_UNAVAILABLE,
        )

    # THE ADAPTER SWAP (phase-49 D1). `qwen-image` and `qwen-360` are one 20B base and one 378 MB
    # LoRA; reloading the base to change the LoRA is 40-90 s of the caller's time to move half a
    # gigabyte. Tried first, and it is only ever an optimisation: any failure falls through to the
    # full load below, having thrown away a pipeline whose adapter state is now unknown.
    if (donor := donor_for(recipe)) is not None:
        started = time.monotonic()

        with _loaded_lock:
            pipe = _loaded.pop(donor, None)
            _base_of.pop(donor, None)

        if pipe is not None:
            try:
                apply_adapters(pipe, recipe, local_files_only=True)
                elapsed = (time.monotonic() - started) * 1000.0

                with _loaded_lock:
                    _loaded[identifier] = pipe
                    _base_of[identifier] = base_key(recipe)

                log(f"{identifier} is ready on {_device} ({elapsed / 1000:.1f}s, adapter swap from {donor})")
                return pipe, elapsed, [donor]
            except Exception as error:  # noqa: BLE001 - an optimisation must never fail a request
                log(f"could not swap adapters {donor} -> {identifier} ({error}); loading from scratch")
                del pipe
                free_vram()

    evicted = evict_for(identifier)

    log(
        f"loading {identifier} ({recipe['repo']}@{recipe['revision'][:12]}, "
        f"{recipe.get('quantization') or 'none'}) on {_device}"
    )

    started = time.monotonic()

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

    if quantization_config(recipe) is None:
        pipe = pipe.to(_device)
    elif _device == "cuda":
        # A bitsandbytes-quantized module is already placed and cannot be cast or moved wholesale;
        # `enable_model_cpu_offload` is the documented path and is also what keeps a 20B transformer
        # and an 8.3B text encoder from needing to be resident at the same instant.
        pipe.enable_model_cpu_offload()

    pipe.set_progress_bar_config(disable=True)

    elapsed = (time.monotonic() - started) * 1000.0

    with _loaded_lock:
        _loaded[identifier] = pipe
        _base_of[identifier] = base_key(recipe)

    log(f"{identifier} is ready on {_device} ({elapsed / 1000:.1f}s)")
    return pipe, elapsed, evicted


def unload_all(reason: str) -> None:
    """
    The ``idle`` hint (phase-48 D3), honoured by the worker rather than by the node.

    The node knows nothing about torch, and a node-side unload would be the node reaching into a
    tool's internals — phase-41 D1's line. What the node knows is that nobody has asked this pool
    for anything in ``idleTimeoutSeconds``; what to do about that is the worker's business. A warm
    process with no weights reloads in ~40 s; a cold one costs that plus the interpreter and the
    import of torch, which is not free.
    """
    with _loaded_lock:
        if not _loaded:
            return

        names = ", ".join(_loaded)
        _loaded.clear()
        _base_of.clear()

    free_vram()
    log(f"freed VRAM held by {names} ({reason})")


# ---- requests ----------------------------------------------------------------------------------


EQUIRECTANGULAR = "equirectangular"


def projection_of(recipe: dict[str, Any]) -> str:
    """
    ``flat`` unless the recipe says otherwise (phase-49 D4). Declared, never inferred from a shape:
    a 2:1 landscape photograph is not a panorama and there is no way to tell from the pixels.
    """
    return str(recipe.get("projection") or "flat").strip().lower() or "flat"


GENERATE = "generate"
EDIT = "edit"
VARIATION = "variation"


def operations_of(recipe: dict[str, Any]) -> list[str]:
    """
    What a recipe can be asked to do (phase-50 D1). Absent means ``["generate"]``.

    The default is what every recipe written before v3.18 means, so a catalogue that predates this
    release keeps declaring exactly what it declared. An unknown operation is dropped rather than
    fatal: a recipe written for a newer worker should degrade to the operations this one has, in the
    same way a node running a capability an older hub never heard of simply is not routed for it.
    """
    declared = recipe.get("operations")

    if not isinstance(declared, list):
        return [GENERATE]

    known = [str(entry).strip().lower() for entry in declared if isinstance(entry, str)]
    return [operation for operation in (GENERATE, EDIT, VARIATION) if operation in known] or [GENERATE]


def resolve_size(
    recipe: dict[str, Any],
    payload: dict[str, Any],
    fallback: tuple[int, int] | None = None,
) -> tuple[int, int]:
    """
    The output geometry, against the recipe's aspect buckets.

    ``fallback`` is the input image's own size on an edit: a caller who sends a 1024x1024 photograph
    and names no ``size`` means "this size", and inventing the recipe's default would silently
    rescale their picture. It is still checked against the buckets — an edit at a size the model was
    not trained on produces the same duplicated limbs a generation does, and finding that out from
    the picture is what this refusal exists to prevent.
    """
    sizes = recipe.get("sizes") or []
    default = (recipe.get("defaults") or {}).get("size")

    width = payload.get("width")
    height = payload.get("height")

    if width is None or height is None:
        if fallback is not None:
            width, height = fallback
        elif default:
            width, height = (int(part) for part in str(default).lower().split("x"))
        else:
            raise ToolError(f"'{recipe['id']}' has no default size and none was given", ERROR_INVALID_REQUEST)

    asked = f"{width}x{height}"

    if sizes and asked not in sizes:
        # THE REASON MATTERS, NOT ONLY THE LIST (phase-49 D3). A wrong size on a flat recipe gives
        # you duplicated limbs, which reads as "this model is bad"; a non-2:1 size on an
        # equirectangular one gives you a picture that looks perfectly fine and wraps wrongly, and
        # the person who finds out is wearing a headset three days later. Say which failure this is.
        if projection_of(recipe) == EQUIRECTANGULAR:
            raise ToolError(
                f"this recipe renders 360-degree equirectangular panoramas, which are always 2:1 "
                f"(360 degrees of longitude over 180 of latitude), so it cannot render {asked}. "
                f"It was trained on: {', '.join(sizes)}. A non-2:1 render does not fail — it wraps "
                f"wrongly in a viewer, which is why this is refused rather than attempted.",
                ERROR_INVALID_REQUEST,
            )

        raise ToolError(
            f"this recipe cannot render {asked}. It was trained on: {', '.join(sizes)}",
            ERROR_INVALID_REQUEST,
        )

    return int(width), int(height)


def apply_trigger(recipe: dict[str, Any], prompt: str) -> tuple[str, bool, str | None]:
    """
    Append the recipe's trigger phrase when it is not already there (phase-49 D2).

    Three options were on the table and two were rejected. SILENTLY REWRITING the prompt is out —
    this repository's most-repeated sentence is that nothing is silently substituted, and a prompt
    is the user's own words. REFUSING a prompt without the trigger is out too: it is pedantry about
    a model whose entire purpose is one thing, and it makes the first request everybody sends a 400.
    So: append when absent, and SAY SO in the response, with the phrase that was added.

    ``autoTrigger: false`` turns it off. The reported flag is present either way, so a client never
    has to infer whether anything happened to its own prompt.
    """
    trigger = str(recipe.get("trigger") or "").strip()

    if not trigger:
        return prompt, False, None

    if recipe.get("autoTrigger") is False or trigger.lower() in prompt.lower():
        return prompt, False, trigger

    return f"{prompt}, {trigger}", True, trigger


def seam_delta(image) -> float | None:
    """
    How far an equirectangular image's left edge is from its right edge, 0-1 (phase-49 D5).

    Mean absolute difference between the first and last columns — which are ADJACENT once the image
    is wrapped onto a sphere — over 255. It is a couple of numpy operations on an array the VAE has
    already produced, which is the whole reason it can be unconditional: a metric that cost a second
    per image would be a flag, and a flag nobody sets is a metric nobody has.

    Returns None rather than raising if anything about the array is unexpected. A measurement that
    could fail a two-minute job would be worse than no measurement.
    """
    try:
        import numpy

        pixels = numpy.asarray(image.convert("RGB"), dtype=numpy.float32)

        if pixels.ndim != 3 or pixels.shape[1] < 2:
            return None

        return float(numpy.abs(pixels[:, 0, :] - pixels[:, -1, :]).mean() / 255.0)
    except Exception:  # noqa: BLE001 - see above
        return None


# How wide the join is worked over, as a fraction of the image's width, and why it is a constant
# rather than a knob. The band has to be wide enough that a correction spread across it is below the
# eye's threshold for a gradient (a few percent of the width is several tens of pixels at any size
# this catalogue renders) and narrow enough that the correction never reaches the middle of the
# picture, where nothing is wrong. A caller who could set it would be tuning a number whose right
# value depends on content they can see and this worker cannot.
SEAM_BLEND_BAND = 0.02

# The inpainting band is wider, because a diffusion pass needs context on both sides of what it is
# repainting: a two-pixel ribbon of mask produces two pixels of new content and a hard edge where it
# meets the old.
SEAM_DIFFUSE_BAND = 0.06

# How far the repair pass moves away from the picture it is repairing. Low on purpose: this is a
# join being closed, not a region being reimagined, and a high strength repaints the band into
# something that no longer matches either side.
SEAM_DIFFUSE_STRENGTH = 0.4


def seam_band(width: int, fraction: float) -> int:
    """Half the worked-over region, in columns. Zero when the image is too narrow to have one."""
    return max(0, min(int(width * fraction), width // 4))


def seam_blend(image):
    """
    Close the join by feathering the discontinuity across a narrow band. numpy, milliseconds, no VRAM.

    The two columns the metric compares are ADJACENT once the image is wrapped onto a sphere, so the
    jump between them is halved and each half is ramped back to zero over ``SEAM_BLEND_BAND``: the
    left edge rises to meet the right, the right falls to meet the left, and both reach exactly the
    midpoint at the join. The extreme columns therefore match by construction, which is why this
    mechanism essentially cannot make the number worse — D4's discard is a guard here and is
    load-bearing for ``diffuse``.

    WHAT IT DOES NOT DO, and the docs say so in these words: it closes a TONAL discontinuity, not a
    STRUCTURAL one. A seam that cuts through a doorway comes back with no visible step in brightness
    and the doorway still not lining up. That is what ``diffuse`` is for.
    """
    try:
        import numpy
        from PIL import Image

        pixels = numpy.asarray(image.convert("RGB"), dtype=numpy.float32)

        if pixels.ndim != 3 or pixels.shape[1] < 2:
            return None

        band = seam_band(pixels.shape[1], SEAM_BLEND_BAND)

        if band < 1:
            return None

        half = (pixels[:, -1, :] - pixels[:, 0, :]) / 2.0
        # 1 at the join, exactly 0 at the far side of the band — so the correction stops rather
        # than being truncated, which would leave a second (much smaller) step where it ended.
        ramp = numpy.linspace(1.0, 0.0, band, dtype=numpy.float32)
        correction = half[:, None, :] * ramp[None, :, None]

        pixels[:, :band, :] += correction
        pixels[:, -band:, :] -= correction[:, ::-1, :]

        return Image.fromarray(numpy.clip(pixels, 0.0, 255.0).astype(numpy.uint8), mode="RGB")
    except Exception as error:  # noqa: BLE001
        log(f"seam blend failed ({type(error).__name__}); keeping the original")
        return None


def seam_diffuse_steps(steps: int) -> int:
    """
    What the repair pass will actually run, known BEFORE the first progress frame (phase-55 D6).

    ``diffusers`` enters an inpainting schedule at ``int(steps * strength)``, so this is the same
    arithmetic phase-50 D3 already meters an edit by — and it has to be computable in advance,
    because a bar that reaches 100% and then starts again is a bar that has lied once.
    """
    return max(1, int(steps * SEAM_DIFFUSE_STRENGTH))


def seam_diffuse(request, recipe, pipe, image, *, prompt, negative, guidance, generator, steps, on_step):
    """
    Upstream's roll-and-inpaint: put the join in the middle of the picture and repaint a band of it.

    A real second pass over the same resident components — ``from_pipe`` reuses the transformer, the
    VAE and the text encoders (phase-50 D1), so it costs steps and no extra VRAM. The roll is what
    makes it possible at all: an inpainting pipeline has no idea the image wraps, and the only way to
    give it both sides of the join at once is to move the join to where it can see it.

    A pipeline family diffusers cannot re-derive fails HERE, naming the class — ``diffuse`` is never
    quietly served as ``blend`` (phase-55 D3).
    """
    import numpy
    from PIL import Image

    inpaint = derived_pipeline(pipe, recipe, inpaint=True)

    width, height = image.size
    shift = width // 2
    band = seam_band(width, SEAM_DIFFUSE_BAND)

    if band < 1:
        return None

    rolled = Image.fromarray(numpy.roll(numpy.asarray(image.convert("RGB")), shift, axis=1))

    mask = numpy.zeros((height, width), dtype=numpy.uint8)
    mask[:, shift - band:shift + band] = 255

    arguments: dict[str, Any] = {
        "prompt": prompt,
        "image": rolled,
        "mask_image": Image.fromarray(mask, mode="L"),
        "strength": SEAM_DIFFUSE_STRENGTH,
        "num_inference_steps": steps,
        str(recipe.get("guidanceParameter") or "guidance_scale"): guidance,
        "generator": generator,
        "callback_on_step_end": on_step,
    }

    if negative:
        arguments["negative_prompt"] = negative

    try:
        result = inpaint(**arguments)
    except TypeError as error:
        # The same retry run_batch makes: the picture is what was asked for and the progress bar is
        # not. Anything else is a real failure and is raised.
        if "callback_on_step_end" not in str(error) and "negative_prompt" not in str(error):
            raise

        arguments.pop("callback_on_step_end", None)
        arguments.pop("negative_prompt", None)
        result = inpaint(**arguments)

    return Image.fromarray(numpy.roll(numpy.asarray(result.images[0].convert("RGB")), -shift, axis=1))


def resolve_seam_repair(recipe: dict[str, Any], payload: dict[str, Any]) -> str | None:
    """
    What this request asked for, checked against what the operator permits (phase-55 D1).

    Three refusals, all of them ``invalid_request`` and all of them naming what to change. Nothing
    here silently degrades to a cheaper mechanism or to nothing at all: a repair that is not the one
    you asked for is the "documented feature that does not work" phase 40 refuses by name.
    """
    asked = str(payload.get("seam_repair") or "").strip().lower()

    if not asked or asked == SEAM_REPAIR_OFF:
        return None

    if asked not in SEAM_REPAIR_MECHANISMS:
        raise ToolError(
            f"seam repair '{asked}' is not a mechanism. Use "
            f"'{SEAM_REPAIR_BLEND}' (a wrapped feather across the join: milliseconds, no steps) or "
            f"'{SEAM_REPAIR_DIFFUSE}' (an inpainting pass over the join: slower, billed as steps).",
            ERROR_INVALID_REQUEST,
        )

    if projection_of(recipe) != EQUIRECTANGULAR:
        raise ToolError(
            f"'{recipe['id']}' renders {projection_of(recipe)} images, which do not wrap and "
            "therefore have no seam to repair.",
            ERROR_INVALID_REQUEST,
        )

    permitted = seam_repair_ceiling()

    if permitted == SEAM_REPAIR_ANY or permitted == asked:
        return asked

    raise ToolError(
        f"seam repair '{asked}' is not permitted on this node: Tools:Image:SeamRepair is "
        f"'{permitted}'. Set it to '{asked}' or to '{SEAM_REPAIR_ANY}' on the node that serves "
        f"'{recipe['id']}'.",
        ERROR_INVALID_REQUEST,
    )


def repair_seam(image, mechanism: str | None, run) -> tuple[Any, dict[str, Any]]:
    """
    Measure, repair, measure again — and keep the original if the number did not fall (phase-55 D4).

    A repair pass that quietly made the seam worse is the one outcome nobody would ever go looking
    for, so the mechanism is reported either way and both numbers ride on the result. ``seamDelta``
    is always the CURRENT image's, so a client that has never heard of this phase reads the field it
    already knew and gets a true answer about the bytes it was handed.
    """
    before = seam_delta(image)

    if before is None:
        return image, {}

    if mechanism is None:
        return image, {"seamDelta": round(before, 5)}

    repaired = run()
    after = seam_delta(repaired) if repaired is not None else None

    if repaired is None or after is None or after >= before:
        log(f"seam repair '{mechanism}' did not lower {before:.5f}; keeping the original")
        return image, {
            "seamDelta": round(before, 5),
            "seamDeltaBefore": round(before, 5),
            "seamRepair": mechanism,
        }

    return repaired, {
        "seamDelta": round(after, 5),
        "seamDeltaBefore": round(before, 5),
        "seamRepair": mechanism,
    }


def resident() -> list[str]:
    with _loaded_lock:
        return list(_loaded)


def handle(request: Request):
    """
    One entry point, dispatched on ``op``. Absent means ``generate``, which is what every request
    made before v3.18 carries; ``edit`` and ``variation`` arrive from the phase-50 client routes,
    and ``pull`` and ``delete`` only from the node's model-command path (phase-48 D4).
    """
    op = str((request.payload or {}).get("op") or GENERATE).lower()

    if op == GENERATE:
        return generate(request)

    if op in (EDIT, VARIATION):
        return edit(request, op)

    if op == "pull":
        return pull(request)

    if op == "delete":
        return delete(request)

    raise ToolError(
        f"unknown op '{op}'; this tool does generate, edit, variation, pull and delete",
        ERROR_INVALID_REQUEST,
    )


def recipe_for(request: Request) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    recipes = load_recipes()
    recipe = recipes.get(request.model)

    if recipe is None:
        raise ToolError(
            f"no recipe '{request.model}' on this node. Available: {', '.join(sorted(recipes)) or 'none'}",
            ERROR_INVALID_REQUEST,
        )

    return recipe, recipes


def require_operation(recipe: dict[str, Any], operation: str) -> None:
    """
    Refuse an operation this recipe does not do, by name (phase-50 D1).

    Not reachable through routing — a recipe that only generates is not declared under
    ``image-edit``, so the hub answers 503 before anything gets here — but a solo caller reaches
    this worker directly, and "FLUX cannot inpaint" is a much better answer than a stack trace out
    of ``AutoPipelineForInpainting``.
    """
    available = operations_of(recipe)

    if operation in available:
        return

    raise ToolError(
        f"'{recipe['id']}' does not do '{operation}'. It does: {', '.join(available)}.",
        ERROR_INVALID_REQUEST,
    )


def clamped_steps(recipe: dict[str, Any], payload: dict[str, Any]) -> int:
    defaults = recipe.get("defaults") or {}
    steps = int(payload.get("steps") or defaults.get("steps") or 30)
    max_steps = int(recipe.get("maxSteps") or 150)

    if steps > max_steps:
        # Clamped rather than refused: a step count above what a recipe is useful at is a request
        # for more quality, not a malformed one, and the response reports what was actually run so
        # the caller is billed for that and can see it.
        log(f"clamping steps {steps} -> {max_steps}")
        steps = max_steps

    return steps


def run_batch(
    request: Request,
    recipe: dict[str, Any],
    pipe: Any,
    *,
    prompt: str,
    negative: str | None,
    count: int,
    seed: Any,
    steps: int,
    reported_steps: int,
    guidance: float,
    width: int,
    height: int,
    geometry: bool = True,
    extra: dict[str, Any] | None = None,
    repair: str | None = None,
    repair_steps: int = 0,
) -> tuple[list[Any], list[dict[str, Any]], list[str], float]:
    """
    The per-image loop, shared by ``generate``, ``edit`` and ``variation``.

    ``reported_steps`` is what the result frame carries and therefore what the fleet meters, and it
    is not always ``steps``: an image-to-image run starts partway down the schedule, so
    ``strength: 0.6`` at 30 steps denoises for 18 of them (phase-50 D3), and a ``diffuse`` seam
    repair adds its own pass on top (phase-55 D5). ``repair_steps`` is how many of ``reported_steps``
    belong to that second pass, which is also what lets the FIRST progress frame carry a total that
    is already right (phase-55 D6).

    ``geometry`` is False for an edit, because the img2img pipelines take their size from the input
    image and **do not accept ``width``/``height`` at all** — the caller's size is honoured by
    resizing the input before it gets here, which is also the only way the mask can be guaranteed to
    still line up with it.
    """
    import torch

    files: list[Any] = []
    images: list[dict[str, Any]] = []
    warnings: list[str] = []
    projection = projection_of(recipe)
    threshold = seam_warn_threshold()
    started = time.monotonic()

    for index in range(count):
        # Every image in a batch gets its own seed, derived from the caller's when they gave one, so
        # `n=4` returns four different pictures AND each one is individually reproducible. A single
        # generator across the batch would make image 3 unreproducible without also rendering 1 and 2.
        image_seed = int(seed) + index if seed is not None else int(torch.seed() % (2**31))
        generator = torch.Generator(device="cpu").manual_seed(image_seed)

        # Per-step progress and cooperative cancellation (phase 47), on the real worker rather than
        # only on the echo one. Both cost a callback: `progress` is a frame the node relays, and
        # `raise_if_cancelled` is what lets a cancel keep the weights — killing the process to
        # abandon one job makes the next caller pay the load.
        #
        # Deliberately NOT decoding the latents to send a preview: that is a VAE decode per step
        # (10-15% of the run) producing intermediate *content*, which nothing here retains.
        def on_step(_pipe, step, _timestep, kwargs, _total=reported_steps, _offset=0):
            request.progress(_offset + step + 1, total_steps=_total)
            request.raise_if_cancelled()
            return kwargs

        arguments: dict[str, Any] = {
            "prompt": prompt,
            "num_inference_steps": steps,

            # WHICH GUIDANCE (phase 49). Qwen's MMDiT pipelines have two: `guidance_scale` is the
            # distilled embedding a step conditions on, and `true_cfg_scale` is real
            # classifier-free guidance against the negative prompt. Passing the wrong one does not
            # error — it produces a picture that is plausible and not what the recipe was tuned
            # for, which is exactly the failure this phase's seam check exists to catch a cousin
            # of. So the recipe names it, defaulting to what every SD-family pipeline calls it.
            str(recipe.get("guidanceParameter") or "guidance_scale"): guidance,
            "generator": generator,
            "callback_on_step_end": on_step,
        }

        if geometry:
            arguments["width"] = width
            arguments["height"] = height

        arguments.update(extra or {})

        if negative:
            arguments["negative_prompt"] = negative

        try:
            result = pipe(**arguments)
        except TypeError as error:
            # A pipeline class that takes neither callback nor negative prompt. Retry once without
            # the optional arguments rather than failing the job: the picture is what was asked for
            # and the progress bar is not.
            if "callback_on_step_end" not in str(error) and "negative_prompt" not in str(error):
                raise

            log(f"{request.model}: {type(pipe).__name__} does not take {error}; retrying without it")
            arguments.pop("callback_on_step_end", None)
            arguments.pop("negative_prompt", None)
            result = pipe(**arguments)

        picture = result.images[0]

        described: dict[str, Any] = {
            "width": width,
            "height": height,
            "steps": reported_steps,
            "seed": image_seed,
            "projection": projection,
        }

        # Measured on every panorama and repaired only for a request that asked (phase-49 D5 as
        # phase 55 amended it). The warning is decided from the FINAL number, because a warning
        # about a seam that was closed would be describing an image nobody has.
        if projection == EQUIRECTANGULAR:
            def repair_run(_picture=picture, _seed_generator=generator):
                if repair == SEAM_REPAIR_BLEND:
                    return seam_blend(_picture)

                return seam_diffuse(
                    request,
                    recipe,
                    pipe,
                    _picture,
                    prompt=prompt,
                    negative=negative,
                    guidance=guidance,
                    generator=_seed_generator,

                    # The same nominal count the base pass ran, from which diffusers takes
                    # `int(steps * strength)` — which is exactly `repair_steps`, computed by the
                    # caller before the first progress frame went out.
                    steps=steps,
                    on_step=lambda p, step, t, kwargs: on_step(
                        p, step, t, kwargs, reported_steps, reported_steps - repair_steps),
                )

            picture, measured = repair_seam(picture, repair, repair_run)
            described.update(measured)

            if (delta := measured.get("seamDelta")) is not None \
                    and threshold > 0 and delta > threshold and "seam" not in warnings:
                warnings.append("seam")

        output = request.output(f"image-{index}.png", "image/png")
        picture.save(output.path, format="PNG")

        files.append(output)
        images.append(described)

    return files, images, warnings, (time.monotonic() - started) * 1000.0


def answer(
    request: Request,
    recipe: dict[str, Any],
    *,
    images: list[dict[str, Any]],
    warnings: list[str],
    steps: int,
    load_ms: float,
    generate_ms: float,
    evicted: list[str],
    prompt_augmented: bool = False,
    trigger: str | None = None,
    extra: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """The result payload, written once so an edit and a generation describe themselves alike."""
    return {
        "model": request.model,
        "steps": steps,
        "device": _device,
        "projection": projection_of(recipe),
        "promptAugmented": prompt_augmented,
        "trigger": trigger,
        "warnings": warnings,
        "images": images,
        # The timing breakdown is what makes a slow request explicable: a swap shows up as seconds
        # of `loadMs` on a request whose `generateMs` is ordinary, and without it the operator's
        # only evidence is "it was slow that one time".
        "timing": {"loadMs": round(load_ms), "generateMs": round(generate_ms)},
        "resident": resident(),
        "evicted": evicted,
        "quantization": recipe.get("quantization") or "none",
        **(extra or {}),
    }


def generate(request: Request):
    recipe, _ = recipe_for(request)
    require_operation(recipe, GENERATE)

    payload = request.payload or {}
    prompt = payload.get("prompt") or ""

    if not prompt:
        raise ToolError("prompt is required", ERROR_INVALID_REQUEST)

    width, height = resolve_size(recipe, payload)
    prompt, prompt_augmented, trigger = apply_trigger(recipe, prompt)
    defaults = recipe.get("defaults") or {}
    steps = clamped_steps(recipe, payload)

    guidance = payload.get("guidance")
    guidance = float(guidance) if guidance is not None else float(defaults.get("guidance", 5.0))

    # Asked for, and permitted, before a single step runs — a refusal after two minutes of GPU is
    # the worst possible place to discover a header the operator never allowed.
    repair = resolve_seam_repair(recipe, payload)
    repair_steps = seam_diffuse_steps(steps) if repair == SEAM_REPAIR_DIFFUSE else 0

    pipe, load_ms, evicted = load(recipe, accepted_licenses())

    files, images, warnings, generate_ms = run_batch(
        request,
        recipe,
        pipe,
        prompt=prompt,
        negative=payload.get("negative_prompt"),
        count=int(payload.get("n") or 1),
        seed=payload.get("seed"),
        steps=steps,
        reported_steps=steps + repair_steps,
        guidance=guidance,
        width=width,
        height=height,
        repair=repair,
        repair_steps=repair_steps,
    )

    # The model, the geometry and the seeds. Not the prompt — the node logs this line's *shape*, and
    # a payload that carried the prompt would put it in the one place rule 7 forbids.
    # The trigger is a recipe constant and may be named; the prompt it was appended to may not.
    log(
        f"{request.model}: {len(images)} image(s) at {width}x{height}, "
        f"{steps + repair_steps} steps on {_device}, "
        f"{projection_of(recipe)}{', trigger appended' if prompt_augmented else ''}"
        f"{', seam repair: ' + repair if repair else ''}"
        f"{', warnings: ' + ', '.join(warnings) if warnings else ''} "
        f"(load {load_ms / 1000:.1f}s, generate {generate_ms / 1000:.1f}s)"
    )

    return answer(
        request,
        recipe,
        images=images,
        warnings=warnings,
        steps=steps + repair_steps,
        load_ms=load_ms,
        generate_ms=generate_ms,
        evicted=evicted,
        prompt_augmented=prompt_augmented,
        trigger=trigger,
    ), files


# ---- editing (phase 50) ------------------------------------------------------------------------


MASK_CONVENTION_OPENAI = "openai"
MASK_CONVENTION_LUMINANCE = "luminance"


def input_image(request: Request, role: str, index: int):
    """
    One of the request's attachments, by the role the edge gave it.

    The edge names the parts ``image`` and ``mask`` rather than passing the filename the caller
    chose: a filename is metadata about somebody's day (phase-42 D5) and it has no business
    crossing the mesh, and a role is what this worker actually needs to know.
    """
    from PIL import Image

    for candidate in request.files:
        if candidate.name == role:
            path = candidate.path
            break
    else:
        if index >= len(request.files):
            return None

        path = request.files[index].path

    try:
        with Image.open(path) as opened:
            return opened.copy()
    except Exception as error:  # noqa: BLE001
        raise ToolError(
            f"the '{role}' attachment is not an image this worker can open ({type(error).__name__})",
            ERROR_INVALID_REQUEST,
        ) from error


def to_inpaint_mask(mask, convention: str):
    """
    Turn the caller's mask into the one ``diffusers`` wants: WHITE is the area to inpaint.

    THE TWO CONVENTIONS ARE OPPOSITE (phase-50 D2). OpenAI's edits API treats a fully TRANSPARENT
    pixel as the area to edit, so the diffusers mask is the *inverse of the alpha channel*.
    ``luminance`` is for a caller who already has a white-is-edit mask and says so — an unknown
    value never reaches here, because the edge refuses it.

    A MASK WITH NO ALPHA IS REFUSED, not guessed at. Under the OpenAI convention a fully opaque
    image means "edit nothing", which no caller has ever intended; reading it as "edit everything"
    would be a silent substitution of the most destructive possible interpretation, and reading it
    as "edit nothing" would return the input image with a 200 on it.
    """
    from PIL import Image, ImageChops

    if convention == MASK_CONVENTION_LUMINANCE:
        return mask.convert("L")

    if mask.mode not in ("RGBA", "LA", "PA") and "transparency" not in mask.info:
        raise ToolError(
            "this mask has no alpha channel, and OpenAI's convention says the TRANSPARENT pixels "
            "are the area to edit — so there is nothing here to edit. Export the mask with an "
            "alpha channel (transparent where the edit goes), or send "
            "X-InferHub-Mask-Convention: luminance if your mask is white where the edit goes.",
            ERROR_INVALID_REQUEST,
        )

    alpha = mask.convert("RGBA").getchannel("A")

    # Inverted: transparent (alpha 0) becomes white (255), which is what the pipeline inpaints.
    return ImageChops.invert(Image.merge("L", (alpha,)))


def edit(request: Request, operation: str):
    """
    ``/v1/images/edits`` and ``/v1/images/variations``, on the same pipeline family (phase 50).

    A mask makes it inpainting and no mask makes it plain image-to-image; a variation is
    image-to-image with no prompt at all, which is why it is a separate op rather than an edit with
    an empty string — "more of this picture" and "change this picture" are different requests and
    only one of them has words in it.
    """
    from PIL import Image

    recipe, _ = recipe_for(request)
    require_operation(recipe, operation)

    payload = request.payload or {}
    source = input_image(request, "image", 0)

    if source is None:
        raise ToolError("this request carried no image to edit", ERROR_INVALID_REQUEST)

    source = source.convert("RGB")
    width, height = resolve_size(recipe, payload, fallback=source.size)

    if source.size != (width, height):
        source = source.resize((width, height), Image.LANCZOS)

    mask = None

    if payload.get("has_mask"):
        raw_mask = input_image(request, "mask", 1)

        if raw_mask is None:
            raise ToolError("this request declared a mask and carried none", ERROR_INVALID_REQUEST)

        if raw_mask.size != (width, height):
            # NOT resized. A mask is a statement about *which pixels*, and scaling one is scaling
            # somebody's selection — the edit would land next to what they chose, which looks like
            # a bad model rather than a bad mask.
            raise ToolError(
                f"the mask is {raw_mask.size[0]}x{raw_mask.size[1]} and the image is {width}x{height}. "
                "A mask names pixels, so it is never rescaled: export it at the image's own size.",
                ERROR_INVALID_REQUEST,
            )

        mask = to_inpaint_mask(raw_mask, str(payload.get("mask_convention") or MASK_CONVENTION_OPENAI))

    defaults = recipe.get("defaults") or {}
    strength = payload.get("strength")

    if strength is None:
        strength = defaults.get("strength")

    if strength is None:
        raise ToolError(
            f"'{recipe['id']}' has no default strength and none was given. Send "
            "X-InferHub-Image-Strength between 0 and 1 (0.75 is a good first try: high enough to "
            "follow the prompt, low enough to keep the picture).",
            ERROR_INVALID_REQUEST,
        )

    strength = float(strength)
    steps = clamped_steps(recipe, payload)

    # What actually runs. `diffusers` enters the schedule at `int(steps * strength)`, so this is
    # both what the progress frames count up to and what the fleet meters (phase-50 D3).
    effective = max(1, int(steps * strength))

    repair = resolve_seam_repair(recipe, payload)
    repair_steps = seam_diffuse_steps(steps) if repair == SEAM_REPAIR_DIFFUSE else 0

    guidance = payload.get("guidance")
    guidance = float(guidance) if guidance is not None else float(defaults.get("guidance", 5.0))

    prompt = "" if operation == VARIATION else (payload.get("prompt") or "")

    if operation == EDIT and not prompt:
        raise ToolError("prompt is required for an edit", ERROR_INVALID_REQUEST)

    prompt, prompt_augmented, trigger = apply_trigger(recipe, prompt) if prompt else (prompt, False, None)

    pipe, load_ms, evicted = load(recipe, accepted_licenses())
    working = derived_pipeline(pipe, recipe, inpaint=mask is not None)

    extra: dict[str, Any] = {"image": source, "strength": strength}

    if mask is not None:
        extra["mask_image"] = mask

    files, images, warnings, generate_ms = run_batch(
        request,
        recipe,
        working,
        prompt=prompt,
        negative=payload.get("negative_prompt"),
        count=int(payload.get("n") or 1),
        seed=payload.get("seed"),
        steps=steps,
        reported_steps=effective + repair_steps,
        guidance=guidance,
        width=width,
        height=height,
        geometry=False,
        extra=extra,
        repair=repair,
        repair_steps=repair_steps,
    )

    # The operation, the geometry, the strength and the steps that ran. Never the prompt, and never
    # anything about the picture that came in — an uploaded image is content in exactly the sense
    # an uploaded recording is (phase-42 D5).
    log(
        f"{request.model}: {operation}, {len(images)} image(s) at {width}x{height}, "
        f"strength {strength:.2f} ({effective} of {steps} steps){', masked' if mask is not None else ''}"
        f"{', seam repair: ' + repair if repair else ''} "
        f"on {_device} (load {load_ms / 1000:.1f}s, generate {generate_ms / 1000:.1f}s)"
    )

    return answer(
        request,
        recipe,
        images=images,
        warnings=warnings,
        steps=effective + repair_steps,
        load_ms=load_ms,
        generate_ms=generate_ms,
        evicted=evicted,
        prompt_augmented=prompt_augmented,
        trigger=trigger,
        extra={"operation": operation, "strength": strength, "masked": mask is not None},
    ), files


def derived_pipeline(pipe: Any, recipe: dict[str, Any], inpaint: bool):
    """
    An inpainting or image-to-image pipeline over the SAME loaded components (phase-50 D1).

    ``from_pipe`` reuses the UNet, the VAE and the text encoders rather than loading a second copy,
    so an edit costs no extra VRAM and no extra seconds — which is the whole reason the edit
    capability does not need a second entry in the residency budget. A recipe that declares ``edit``
    against a pipeline family diffusers cannot re-derive fails HERE, naming the class, rather than
    somewhere inside a forward pass.
    """
    from diffusers import AutoPipelineForImage2Image, AutoPipelineForInpainting

    factory = AutoPipelineForInpainting if inpaint else AutoPipelineForImage2Image

    try:
        derived = factory.from_pipe(pipe)
    except Exception as error:  # noqa: BLE001
        raise ToolError(
            f"'{recipe['id']}' declares this operation, but diffusers cannot derive "
            f"{'an inpainting' if inpaint else 'an image-to-image'} pipeline from "
            f"{type(pipe).__name__} ({type(error).__name__}: {error}). Remove the operation from "
            "the recipe, or point it at a pipeline family that supports it.",
            ERROR_INVALID_REQUEST,
        ) from error

    derived.set_progress_bar_config(disable=True)
    return derived


def pull(request: Request):
    """
    Fetch a recipe's weights and prove them loadable, reporting progress as chunks (phase-48 D4).

    THE PERCENTAGE IS DELIBERATELY ABSENT. `huggingface_hub` gives no download callback, and a
    denominator this worker would have to guess is a number a dashboard would happily plot —
    phase-28 D5's line. What it reports instead is a fact: how many mebibytes have landed in the
    cache since the pull started.
    """
    recipe, _ = recipe_for(request)
    accepted = accepted_licenses()

    if not is_licensed(recipe, accepted):
        raise ToolError(
            f"'{recipe['id']}' is licensed under '{licence_id(recipe)}', which this node has not "
            f"accepted. Add \"{licence_id(recipe)}\" to Tools:Image:AcceptedLicenses if you have "
            f"read it ({licence_of(recipe).get('url') or 'see the model repository'}).",
            ERROR_MODEL_UNAVAILABLE,
        )

    if not flag("INFERHUB_ALLOW_MODEL_DOWNLOAD"):
        raise ToolError(
            f"this node may not fetch weights: Tools:AllowModelDownload is false. Pre-fetch "
            f"'{recipe['id']}' out of band with:\n  {prefetch_command(recipe)}",
            ERROR_MODEL_UNAVAILABLE,
        )

    if is_ready(recipe):
        request.chunk({"status": "already-present", "model": recipe["id"]})
        return {"model": recipe["id"], "status": "already-present", "ready": True}

    request.chunk({"status": "downloading", "model": recipe["id"], "repo": recipe["repo"]})

    baseline = cache_bytes()
    stop = threading.Event()

    def watch() -> None:
        while not stop.wait(5.0):
            landed = max(0, cache_bytes() - baseline) // (1024 ** 2)
            request.chunk({"status": f"downloading ({landed} MiB)", "model": recipe["id"], "mib": landed})

    monitor = threading.Thread(target=watch, daemon=True, name="pull-progress")
    monitor.start()

    try:
        fetch(recipe)
    finally:
        stop.set()

    request.chunk({"status": "verifying", "model": recipe["id"]})
    log(f"'{recipe['id']}' is ready")
    redeclare()

    return {"model": recipe["id"], "status": "ready", "ready": True}


def delete(request: Request):
    """Drop the readiness marker and the cached revision. Never touches another recipe's blobs."""
    recipe, _ = recipe_for(request)

    marker = ready_marker(recipe)

    if os.path.exists(marker):
        os.remove(marker)

    removed = 0

    try:
        from huggingface_hub import scan_cache_dir

        cache = scan_cache_dir()
        revisions = [
            revision.commit_hash
            for repo in cache.repos
            if repo.repo_id == recipe["repo"]
            for revision in repo.revisions
        ]

        if revisions:
            strategy = cache.delete_revisions(*revisions)
            removed = strategy.expected_freed_size
            strategy.execute()
    except Exception as error:  # noqa: BLE001 - a cache we cannot scan is not a failed command
        log(f"could not remove '{recipe['id']}' from the cache: {type(error).__name__}: {error}")

    with _loaded_lock:
        _loaded.pop(recipe["id"], None)
        _base_of.pop(recipe["id"], None)

    free_vram()
    log(f"'{recipe['id']}' removed ({removed // (1024 ** 2)} MiB freed)")
    redeclare()

    return {"model": recipe["id"], "status": "deleted", "freedBytes": removed}


def capability_frames(recipes: dict[str, dict[str, Any]], ready: list[str]) -> list[dict[str, Any]]:
    """
    The two kinds this worker declares, split by what each ready recipe can do (phase-50 D1).

    ``image`` is the generators and ``image-edit`` is the ones that also edit or vary. Two kinds
    rather than one kind with an operation list, because the router filters on ``(kind, model)`` and
    nothing else — teaching it to read a nested operation set would mean teaching the affinity, the
    queue and the saturation logic the same thing.

    **Both frames are always sent, empty ones included.** An empty list is a declaration that this
    node serves nothing under that kind, which is exactly what a box holding only ``flux-schnell``
    should say about editing; omitting the frame would leave the node's previous declaration
    standing, and a node that quietly kept offering an edit it can no longer do is a 502 waiting for
    somebody.
    """
    editable = [i for i in ready if {EDIT, VARIATION} & set(operations_of(recipes[i]))]
    generating = [i for i in ready if GENERATE in operations_of(recipes[i])]

    return [
        {"kind": "image", "models": generating},
        {"kind": "image-edit", "models": editable},
    ]


def describe_offering(recipes: dict[str, dict[str, Any]], ready: list[str]) -> str:
    frames = capability_frames(recipes, ready)
    editable = frames[1]["models"]

    return (
        f"offering recipes: {', '.join(frames[0]['models']) or 'none'}"
        f" (editing: {', '.join(editable) or 'none'})"
    )


def redeclare() -> None:
    """
    Re-report what is ready, after a pull or a delete moved the answer.

    The node re-applies its own narrowing clamp to this and re-reports the node to its coordinator
    (v3.14.1). Widening is still refused there: every id here is a recipe file the operator put on
    the box, and this can only ever be a subset of what the manifest granted.
    """
    if _worker is None:
        return

    recipes = load_recipes()
    ready = [i for i in _offerable if i in recipes and is_ready(recipes[i])]
    log(describe_offering(recipes, ready))
    _worker.redeclare(capability_frames(recipes, ready))


def cache_bytes() -> int:
    total = 0

    for root, _dirs, names in os.walk(os.path.join(hf_home(), "hub")):
        for name in names:
            try:
                total += os.path.getsize(os.path.join(root, name))
            except OSError:
                continue

    return total


# ---- startup -----------------------------------------------------------------------------------


def describe_fetch_failure(recipe: dict[str, Any], error: Exception) -> str:
    """
    Say what to do about it, not only what happened (v3.16.2).

    A GATED REPOSITORY is the case worth spelling out, and it was found by running the published
    image: `black-forest-labs/FLUX.1-schnell` is Apache-2.0 — so InferHub's licence gate lets it
    through, correctly — and Hugging Face *still* requires you to accept terms on the model page and
    present a token. The raw error for that is ``GatedRepoError: 401 Client Error`` plus a request
    id, which reads as "the model is gone" rather than "click accept".

    It is deliberately a different sentence from the licence refusal. Accepting a licence tells THIS
    NODE it may run a model; a token is how HUGGING FACE decides whether to hand the weights over,
    and telling somebody to edit `AcceptedLicenses` when the fix is a token wastes their afternoon.
    """
    name = type(error).__name__
    detail = str(error)[:300]

    if "GatedRepo" in name or "401" in detail or "gated" in detail.lower():
        return (
            f"{name}: this repository is GATED on Hugging Face. Accept the terms at "
            f"https://huggingface.co/{recipe['repo']} with the account you are using, then set "
            f"Tools:Image:HuggingFaceToken to a read token. This is separate from "
            f"Tools:Image:AcceptedLicenses, which is this node's own consent and does not carry a "
            f"credential."
        )

    return f"{name}: {detail}"


def fetch_order(recipes: dict[str, dict[str, Any]], missing: list[str]) -> list[str]:
    """
    Cheapest first, and anything already in the cache before anything that is not (v3.16.2).

    ALPHABETICAL ORDER WAS A BUG. `missing` used to be walked in sorted order, so on a box where
    `sd15` and `sdxl` were already downloaded and only needed re-proving, a 40 GB `qwen-image`
    fetch queued ahead of them and the node declared NOTHING for as long as it ran. "The model
    appears when it is ready" stops being true the moment an unrelated large model is in front of
    it — and the one somebody is waiting for is almost never the alphabetically first.

    So: already-downloaded first (seconds to re-prove), then by declared VRAM ascending as the only
    proxy for download size a recipe carries.
    """
    hub = os.path.join(hf_home(), "hub")

    def cached(identifier: str) -> bool:
        repo = recipes[identifier]["repo"].replace("/", "--")
        return os.path.isdir(os.path.join(hub, f"models--{repo}"))

    return sorted(missing, key=lambda i: (not cached(i), int(recipes[i].get("vramMiB") or 0), i))


def prefetch_missing(worker: Worker, recipes: dict[str, dict[str, Any]], offerable: list[str]) -> None:
    """
    Fetch, prove, and RE-DECLARE — on a background thread, never inside a request (v3.14.1).

    Each model that lands re-declares immediately rather than waiting for the batch, because the
    first one is usually the one somebody is waiting for.
    """
    missing = fetch_order(recipes, [i for i in offerable if not is_ready(recipes[i])])

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
                f"(variant={recipe.get('variant') or 'default'}, "
                f"quantization={recipe.get('quantization') or 'none'}) into {hf_home()}. "
                "This runs in the background; the recipe is offered when it completes."
            )
            fetch(recipe)
        except Exception as error:  # noqa: BLE001 - one bad model must not stop the others
            log(f"could not fetch '{identifier}': {describe_fetch_failure(recipe, error)}")
            continue

        ready = [i for i in offerable if is_ready(recipes[i])]
        log(f"'{identifier}' is ready; {describe_offering(recipes, ready)}")
        worker.redeclare(capability_frames(recipes, ready))


def main() -> None:
    global _device, _offerable, _worker

    recipes = load_recipes()

    if not recipes:
        log(f"no recipes found under {recipes_directory()}, so this worker offers nothing")
        Worker(capabilities=capability_frames({}, [])).run(handle)
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

    _offerable = offered(recipes, _device)
    offerable = _offerable
    ready = [identifier for identifier in offerable if is_ready(recipes[identifier])]

    log(
        f"catalogue: {len(recipes)} recipe(s), {len(offerable)} offerable on this box, "
        f"at most {resident_limit()} resident at a time"
    )
    log(
        describe_offering(recipes, ready)
        + (f" (fetching: {', '.join(i for i in offerable if i not in ready)})" if len(ready) < len(offerable) else "")
    )

    worker = Worker(
        capabilities=capability_frames(recipes, ready),
        on_idle=lambda: unload_all("idle"),
        vram_total_mib=_vram_total_mib,
    )
    _worker = worker

    # Daemon: a fetch in flight must not keep the process alive past a SIGTERM. An interrupted
    # download resumes from its `.incomplete` blob on the next start, so nothing is lost.
    threading.Thread(
        target=prefetch_missing, args=(worker, recipes, offerable), daemon=True, name="prefetch"
    ).start()

    worker.run(handle)


if __name__ == "__main__":
    main()
